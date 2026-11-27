#!/usr/bin/env python3
"""Offline synthetic tests for build/package-release.py and tools/deploy-remote.py."""
from __future__ import annotations

import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import threading
import unittest


REPO_ROOT = Path(__file__).resolve().parents[2]
PACKAGE_SCRIPT = REPO_ROOT / 'build' / 'package-release.py'
DEPLOY_SCRIPT = REPO_ROOT / 'tools' / 'deploy-remote.py'
VERIFY_WEB_SH = REPO_ROOT / 'tools' / 'verify-web-deploy.sh'


FAKE_DOCKER_TEMPLATE = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

state_file = Path(os.environ['FAKE_DOCKER_STATE'])
state = json.loads(state_file.read_text(encoding='utf-8'))
args = sys.argv[1:]

def save():
    state_file.write_text(json.dumps(state), encoding='utf-8')

if args[:2] == ['inspect', '--format']:
    fmt = args[2]
    target = args[3]
    if target == 'qqchat-napcat':
        if state.get('napcat_missing'):
            sys.exit(1)
        print(state['napcat_id'])
        sys.exit(0)
    if target == 'qqchat-bot':
        if '{{.Image}}' in fmt:
            print(state['bot_image'])
        elif 'NetworkSettings.Networks' in fmt:
            print(json.dumps({n: {} for n in state['bot_networks']}))
        elif 'State.Health.Status' in fmt:
            print(state['bot_health'])
        else:
            print(state['napcat_id'])
        sys.exit(0)

if args[:2] == ['image', 'inspect']:
    tag = args[4] if len(args) > 4 else args[-1]
    if tag in state['images']:
        print(state['images'][tag])
        sys.exit(0)
    sys.exit(1)

if args and args[0] == 'build':
    iid_idx = args.index('--iidfile')
    iid_path = Path(args[iid_idx + 1])
    tag = args[args.index('-t') + 1]
    new_id = 'sha256:' + ('b' * 64)
    state['images'][tag] = new_id
    iid_path.write_text(new_id, encoding='utf-8')
    save()
    sys.exit(0)

if args and args[0] == 'create':
    print('fake-container-1')
    sys.exit(0)

if args and args[0] == 'cp':
    dst = Path(args[2])
    dst.write_bytes(b'fake-dll-bytes')
    sys.exit(0)

if args and args[0] == 'tag':
    src, dst = args[1], args[2]
    resolved = state['images'].get(src, src)
    state['images'][dst] = resolved
    save()
    sys.exit(0)

if args[:1] == ['stop']:
    state['bot_running'] = False
    save()
    sys.exit(0)

if args[:1] == ['start']:
    state['bot_running'] = True
    save()
    sys.exit(0)

if args and args[0] == 'run':
    if '--check-plugin-state' in args:
        sys.exit(int(state.get('preflight_rc', 0)))
    sys.exit(0)

if args and args[0] == 'compose':
    if 'up' in args:
        state['bot_image'] = state['images'].get('qqchat-agent:latest', state['bot_image'])
        # Simulate compose dropping external network unless reattached.
        state['bot_networks'] = ['qqchat']
        state['bot_running'] = True
        if state['bot_image'].endswith('b' * 16):
            state['active_mode'] = 'candidate'
            if state.get('fail_candidate_health'):
                state['bot_health'] = 'running unhealthy'
            else:
                state['bot_health'] = 'running healthy'
        else:
            state['active_mode'] = 'rollback'
            state['bot_health'] = 'running healthy'
        save()
    sys.exit(0)

if args[:2] == ['network', 'connect']:
    net = args[2]
    if net not in state['bot_networks']:
        state['bot_networks'].append(net)
    save()
    sys.exit(0)

if args and args[0] == 'rm':
    sys.exit(0)

sys.exit(0)
'''


FAKE_VERIFY_WEB = r'''#!/usr/bin/env python3
import os
from pathlib import Path
import sys

web_dir = Path(sys.argv[2])
site_host = sys.argv[3]
assert (web_dir / 'index.html').is_file(), 'missing index.html'
assert site_host == os.environ['EXPECTED_SITE_HOST']
if os.environ.get('FAIL_VERIFY_WEB') == '1':
    sys.exit(1)
sys.exit(0)
'''


class _MockServerHandler(http.server.BaseHTTPRequestHandler):
    server_version = 'MockCD/1.0'

    def log_message(self, format, *args):
        return

    def do_GET(self):
        cfg = self.server.cfg  # type: ignore[attr-defined]
        state = json.loads(Path(cfg['state_file']).read_text(encoding='utf-8'))
        if self.path == '/healthz':
            if state.get('active_mode') == 'candidate' and cfg.get('fail_healthz'):
                self.send_response(500)
                self.end_headers()
                self.wfile.write(b'{"status":"error"}')
                return
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({'status': 'ok', 'version': cfg['expected_version']}).encode('utf-8'))
            return
        if self.path == '/readyz':
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"ready":true,"onebot":"connected"}')
            return
        if self.path.startswith('/web/assets/__botagent_cd_missing__'):
            cookie = self.headers.get('Cookie', '')
            if cookie == cfg['cookie']:
                self.send_response(404)
                self.end_headers()
                return
            self.send_response(303)
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            return
        if self.path.startswith('/web/assets/'):
            if cfg.get('bad_unauth_200'):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'/* bad */')
                return
            self.send_response(303)
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            return
        self.send_response(404)
        self.end_headers()


def _make_synthetic_repo(root: Path, version: str = '1.5.0') -> tuple[Path, Path, Path]:
    (root / 'build').mkdir(parents=True, exist_ok=True)
    (root / 'Directory.Build.props').write_text(f'<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>\n', encoding='utf-8')
    (root / 'CHANGELOG.md').write_text(f'# Changelog\n\n## [Unreleased]\n\n## [{version}] - 2026-11-27\n\n- Test release.\n', encoding='utf-8')
    (root / 'Dockerfile.app').write_text('FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:' + ('0' * 64) + '\n', encoding='utf-8')
    shutil.copyfile(REPO_ROOT / 'build' / 'release-install.template.md', root / 'build' / 'release-install.template.md')

    app_dir = root / 'prebuilt-app'
    app_dir.mkdir()
    (app_dir / 'BotAgent.Headless.dll').write_bytes(b'dll')
    (app_dir / 'BotAgent.Headless').write_bytes(b'bin')

    web_dir = root / 'prebuilt-web'
    (web_dir / 'assets').mkdir(parents=True)
    (web_dir / 'index.html').write_text('<!doctype html><script type="module" src="/web/assets/app-1234.js"></script>', encoding='utf-8')
    (web_dir / 'assets' / 'app-1234.js').write_text('console.log("ok");', encoding='utf-8')
    return root, app_dir, web_dir


class CdPipelineTests(unittest.TestCase):
    def test_package_release_deterministic_and_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory(prefix='cd-pkg-test-') as tmp:
            base = Path(tmp)
            src, app_dir, web_dir = _make_synthetic_repo(base / 'repo', '1.5.0')
            out1 = base / 'out1'
            out2 = base / 'out2'
            common = [
                sys.executable,
                str(PACKAGE_SCRIPT),
                '--source', str(src),
                '--commit', 'a' * 40,
                '--tree', 'b' * 40,
                '--epoch', '1764200000',
                '--ref', 'refs/tags/v1.5.0',
                '--prebuilt-app', str(app_dir),
                '--prebuilt-web', str(web_dir),
            ]
            subprocess.run([*common, '--output', str(out1)], check=True, capture_output=True)
            subprocess.run([*common, '--output', str(out2)], check=True, capture_output=True)

            sha1 = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (out1 / 'assets').iterdir()}
            sha2 = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (out2 / 'assets').iterdir()}
            self.assertEqual(sha1, sha2)
            self.assertEqual(len(sha1), 6)

            # Tag mismatch must fail-closed.
            bad_tag = subprocess.run(
                [
                    sys.executable, str(PACKAGE_SCRIPT),
                    '--source', str(src), '--output', str(base / 'out-bad-tag'),
                    '--commit', 'a' * 40, '--tree', 'b' * 40, '--epoch', '1764200000',
                    '--ref', 'refs/tags/v9.9.9',
                    '--prebuilt-app', str(app_dir), '--prebuilt-web', str(web_dir),
                ],
                capture_output=True, text=True,
            )
            self.assertNotEqual(bad_tag.returncode, 0)

            # Debug .pdb in app output must fail-closed.
            bad_app = base / 'bad-app'
            shutil.copytree(app_dir, bad_app)
            (bad_app / 'BotAgent.Headless.pdb').write_bytes(b'pdb')
            bad_pdb = subprocess.run(
                [
                    sys.executable, str(PACKAGE_SCRIPT),
                    '--source', str(src), '--output', str(base / 'out-bad-pdb'),
                    '--commit', 'a' * 40, '--tree', 'b' * 40, '--epoch', '1764200000',
                    '--ref', 'refs/tags/v1.5.0',
                    '--prebuilt-app', str(bad_app), '--prebuilt-web', str(web_dir),
                ],
                capture_output=True, text=True,
            )
            self.assertNotEqual(bad_pdb.returncode, 0)

            # Web asset outside /web/ must fail-closed.
            bad_web = base / 'bad-web'
            shutil.copytree(web_dir, bad_web)
            (bad_web / 'index.html').write_text('<script src="/assets/bad.js"></script>', encoding='utf-8')
            bad_ref = subprocess.run(
                [
                    sys.executable, str(PACKAGE_SCRIPT),
                    '--source', str(src), '--output', str(base / 'out-bad-web'),
                    '--commit', 'a' * 40, '--tree', 'b' * 40, '--epoch', '1764200000',
                    '--ref', 'refs/tags/v1.5.0',
                    '--prebuilt-app', str(app_dir), '--prebuilt-web', str(bad_web),
                ],
                capture_output=True, text=True,
            )
            self.assertNotEqual(bad_ref.returncode, 0)

    def _run_deploy_scenario(
        self,
        *,
        state_overrides: dict[str, object] | None = None,
        server_overrides: dict[str, object] | None = None,
    ) -> tuple[subprocess.CompletedProcess[str], Path, Path, Path, dict[str, object]]:
        tmp = Path(tempfile.mkdtemp(prefix='cd-deploy-test-'))
        src, app_dir, web_dir = _make_synthetic_repo(tmp / 'repo', '1.5.0')
        bundle_root = tmp / 'bundle'
        subprocess.run(
            [
                sys.executable, str(PACKAGE_SCRIPT),
                '--source', str(src), '--output', str(bundle_root),
                '--commit', 'a' * 40, '--tree', 'b' * 40, '--epoch', '1764200000',
                '--ref', 'refs/tags/v1.5.0',
                '--prebuilt-app', str(app_dir), '--prebuilt-web', str(web_dir),
            ],
            check=True, capture_output=True,
        )

        app_dir_host = tmp / 'opt_qqchat'
        data_dir_host = app_dir_host / 'data'
        (data_dir_host / 'plugins').mkdir(parents=True)
        (data_dir_host / 'logs').mkdir(parents=True)
        (data_dir_host / 'logs' / 'qqchat.log').write_text('ERROR line before deploy\n', encoding='utf-8')
        (data_dir_host / 'plugins' / 'sample.txt').write_text('old-plugin', encoding='utf-8')

        db_path = data_dir_host / 'botagent.db'
        conn = sqlite3.connect(str(db_path))
        conn.execute('PRAGMA user_version = 15')
        conn.execute('CREATE TABLE marker (val TEXT)')
        conn.execute("INSERT INTO marker VALUES ('before-upgrade')")
        conn.commit()
        conn.close()

        web_dir_host = tmp / 'var_www_web'
        web_dir_host.mkdir(parents=True)
        (web_dir_host / 'index.html').write_text('OLD-WEB', encoding='utf-8')
        (app_dir_host / 'docker-compose.yml').write_text('services: {}\n', encoding='utf-8')

        caddyfile = tmp / 'Caddyfile'
        caddyfile.write_text('@authed header Cookie *botagent_auth=secret-cookie*\n', encoding='utf-8')

        state_file = tmp / 'docker_state.json'
        init_state = {
            'napcat_id': 'napcat-abc 2026-11-12T04:50:54Z',
            'bot_image': 'sha256:' + ('a' * 64),
            'bot_networks': ['qqchat', 'qqchat6-net'],
            'bot_health': 'running healthy',
            'bot_running': True,
            'active_mode': 'old',
            'images': {'qqchat-agent:latest': 'sha256:' + ('a' * 64)},
        }
        if state_overrides:
            init_state.update(state_overrides)
        state_file.write_text(json.dumps(init_state), encoding='utf-8')

        fake_docker = tmp / 'fake_docker.py'
        fake_docker.write_text(FAKE_DOCKER_TEMPLATE, encoding='utf-8')
        fake_verify = tmp / 'fake_verify_web.py'
        fake_verify.write_text(FAKE_VERIFY_WEB, encoding='utf-8')
        if os.name == 'nt':
            docker_cmd = tmp / 'fake_docker.cmd'
            docker_cmd.write_text(f'@echo off\r\n"{sys.executable}" "{fake_docker}" %*\r\n', encoding='utf-8')
            verify_cmd = tmp / 'fake_verify.cmd'
            verify_cmd.write_text(f'@echo off\r\n"{sys.executable}" "{fake_verify}" %*\r\n', encoding='utf-8')
        else:
            docker_cmd = fake_docker
            verify_cmd = fake_verify
            os.chmod(docker_cmd, 0o755)
            os.chmod(verify_cmd, 0o755)

        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), _MockServerHandler)
        cfg = {
            'state_file': str(state_file),
            'expected_version': '1.5.0',
            'cookie': 'botagent_auth=secret-cookie',
        }
        if server_overrides:
            cfg.update(server_overrides)
        server.cfg = cfg  # type: ignore[attr-defined]
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        port = server.server_address[1]
        base_url = f'http://127.0.0.1:{port}'
        site_host = 'secret-panel.example.test'
        receipt_path = tmp / 'receipt.json'
        env = dict(
            os.environ,
            FAKE_DOCKER_STATE=str(state_file),
            BOTAGENT_DOCKER_BIN=str(docker_cmd),
            BOTAGENT_VERIFY_WEB_BIN=str(verify_cmd),
            DEPLOY_SITE_HOST=site_host,
            EXPECTED_SITE_HOST=site_host,
        )
        proc = subprocess.run(
            [
                sys.executable, str(DEPLOY_SCRIPT), 'deploy',
                '--bundle-dir', str(bundle_root / 'assets'),
                '--app-dir', str(app_dir_host),
                '--web-dir', str(web_dir_host),
                '--caddyfile', str(caddyfile),
                '--health-url', base_url,
                '--caddy-url', base_url,
                '--expected-version', '1.5.0',
                '--min-free-mb', '1',
                '--timeout-sec', '10',
                '--public-log-safe',
                '--receipt-out', str(receipt_path),
            ],
            env=env, capture_output=True, text=True, encoding='utf-8',
        )
        server.shutdown()
        server.server_close()
        final_state = json.loads(state_file.read_text(encoding='utf-8'))
        return proc, tmp, app_dir_host, web_dir_host, final_state

    def test_deploy_happy_path_and_public_log_redaction(self) -> None:
        proc, tmp, app_dir, web_dir, final_state = self._run_deploy_scenario()
        try:
            self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
            self.assertNotIn('secret-panel.example.test', proc.stdout)
            self.assertNotIn('secret-panel.example.test', (tmp / 'receipt.json').read_text(encoding='utf-8'))
            self.assertIn('qqchat6-net', final_state['bot_networks'])
            self.assertIn('/web/assets/app-1234.js', (web_dir / 'index.html').read_text(encoding='utf-8'))
            rb_dirs = list(app_dir.glob('rollback-release-*'))
            self.assertEqual(len(rb_dirs), 1)
            snap_db = rb_dirs[0] / 'botagent.db'
            self.assertTrue(snap_db.is_file())
            conn = sqlite3.connect(str(snap_db))
            self.assertEqual(conn.execute('PRAGMA integrity_check').fetchone()[0], 'ok')
            self.assertEqual(conn.execute('PRAGMA user_version').fetchone()[0], 15)
            conn.close()
            with tarfile.open(rb_dirs[0] / 'plugins-before.tar', 'r') as tar:
                self.assertIn('sample.txt', tar.getnames())
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_deploy_rejects_napcat_missing_or_preflight_fail_or_web_gate_or_health_rollback(self) -> None:
        # 1. Missing NapCat fails closed.
        proc, tmp, _, _, _ = self._run_deploy_scenario(state_overrides={'napcat_missing': True})
        try:
            self.assertNotEqual(proc.returncode, 0)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        # 2. Plugin preflight non-zero restarts original container without overwriting :latest.
        proc, tmp, _, web_dir, final_state = self._run_deploy_scenario(state_overrides={'preflight_rc': 2})
        try:
            self.assertNotEqual(proc.returncode, 0)
            self.assertTrue(final_state['bot_running'])
            self.assertEqual(final_state['images']['qqchat-agent:latest'], 'sha256:' + ('a' * 64))
            self.assertEqual((web_dir / 'index.html').read_text(encoding='utf-8'), 'OLD-WEB')
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        # 3. Unauthenticated JS returning 200 rolls back frontend immediately.
        proc, tmp, _, web_dir, _ = self._run_deploy_scenario(server_overrides={'bad_unauth_200': True})
        try:
            self.assertNotEqual(proc.returncode, 0)
            self.assertEqual((web_dir / 'index.html').read_text(encoding='utf-8'), 'OLD-WEB')
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

        # 4. Candidate /healthz 500 triggers full auto-rollback (image + DB + web).
        proc, tmp, app_dir, web_dir, final_state = self._run_deploy_scenario(
            state_overrides={'fail_candidate_health': True},
            server_overrides={'fail_healthz': True},
        )
        try:
            self.assertNotEqual(proc.returncode, 0)
            self.assertEqual(final_state['active_mode'], 'rollback')
            self.assertEqual((web_dir / 'index.html').read_text(encoding='utf-8'), 'OLD-WEB')
            conn = sqlite3.connect(str(app_dir / 'data/botagent.db'))
            row = conn.execute('SELECT val FROM marker').fetchone()
            conn.close()
            self.assertEqual(row[0], 'before-upgrade')
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    unittest.main(verbosity=2)
