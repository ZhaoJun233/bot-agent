#!/usr/bin/env python3
"""Remote atomic dual-rollback deployment and rollback engine for BotAgent."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_NO_REDIRECT_OPENER = urllib.request.build_opener(_NoRedirect())


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def docker_bin() -> str:
    return os.environ.get('BOTAGENT_DOCKER_BIN', 'docker')


def run_proc(cmd: list[str], *, cwd: Path | None = None, env: dict[str, str] | None = None, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        cmd,
        cwd=str(cwd) if cwd else None,
        env=env,
        capture_output=True,
        text=True,
        encoding='utf-8',
        errors='replace',
        timeout=timeout,
    )


def docker(args: list[str], *, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    return run_proc([docker_bin(), *args], timeout=timeout)


def log_step(msg: str, site_host: str = '', public_safe: bool = False) -> None:
    text = msg.replace(site_host, '<redacted-host>') if (public_safe and site_host) else msg
    print(text, flush=True)


def verify_sha256sums(bundle_dir: Path) -> dict[str, str]:
    sums_path = bundle_dir / 'SHA256SUMS.txt'
    if not sums_path.is_file():
        raise RuntimeError('Missing SHA256SUMS.txt in bundle directory')
    verified: dict[str, str] = {}
    for raw in sums_path.read_text(encoding='utf-8').splitlines():
        line = raw.strip()
        if not line:
            continue
        parts = line.split(None, 1)
        if len(parts) != 2:
            raise RuntimeError(f'Malformed SHA256SUMS.txt line: {line}')
        expected, name = parts[0].lower(), parts[1].strip().lstrip('*')
        target = bundle_dir / name
        if not target.is_file():
            raise RuntimeError(f'Missing bundle asset listed in SHA256SUMS.txt: {name}')
        actual = sha256_file(target)
        if actual != expected:
            raise RuntimeError(f'SHA256 mismatch for {name}: expected {expected}, got {actual}')
        verified[name] = actual
    if not verified:
        raise RuntimeError('SHA256SUMS.txt contains no entries')
    return verified


def resolve_bundle_assets(bundle_dir: Path) -> tuple[Path | None, Path | None, Path | None]:
    app_tar = next(iter(sorted(bundle_dir.glob('botagent-*-linux-x64.tar.gz'))), None)
    if app_tar is None and (bundle_dir / 'app.tar.gz').is_file():
        app_tar = bundle_dir / 'app.tar.gz'
    web_tar = next(iter(sorted(bundle_dir.glob('botagent-*-web.tar.gz'))), None)
    if web_tar is None and (bundle_dir / 'web.tar.gz').is_file():
        web_tar = bundle_dir / 'web.tar.gz'
    dockerfile = bundle_dir / 'Dockerfile.app.txt'
    if not dockerfile.is_file():
        alt = bundle_dir / 'Dockerfile.app'
        dockerfile = alt if alt.is_file() else None
    return app_tar, web_tar, dockerfile


def container_identity(name: str) -> str | None:
    res = docker(['inspect', '--format', '{{.Id}} {{.State.StartedAt}}', name], timeout=20)
    if res.returncode != 0:
        return None
    return res.stdout.strip()


def container_image_id(name: str) -> str:
    res = docker(['inspect', '--format', '{{.Image}}', name], timeout=20)
    if res.returncode != 0 or not res.stdout.strip():
        raise RuntimeError(f'Cannot inspect image ID for container {name}')
    return res.stdout.strip()


def container_networks(name: str) -> list[str]:
    res = docker(['inspect', '--format', '{{json .NetworkSettings.Networks}}', name], timeout=20)
    if res.returncode != 0 or not res.stdout.strip():
        return []
    try:
        data = json.loads(res.stdout.strip())
        return sorted(data.keys()) if isinstance(data, dict) else []
    except json.JSONDecodeError:
        return []


def check_free_space(path: Path, min_free_mb: int) -> None:
    check_path = path if path.exists() else path.parent
    usage = shutil.disk_usage(check_path)
    free_mb = usage.free // (1024 * 1024)
    if free_mb < min_free_mb:
        raise RuntimeError(f'Insufficient free disk space at {check_path}: {free_mb} MiB < {min_free_mb} MiB')


def read_caddy_cookie(caddyfile: Path) -> str:
    if not caddyfile.is_file():
        return ''
    text = caddyfile.read_text(encoding='utf-8', errors='replace')
    m = re.search(r'@authed\s+header\s+Cookie\s+([^\s\n]+)', text)
    if not m:
        return ''
    return m.group(1).strip('*"\'')


def http_request(url: str, *, host_header: str = '', cookie: str = '', timeout: int = 8) -> tuple[int, dict[str, str], str]:
    headers: dict[str, str] = {}
    if host_header:
        headers['Host'] = host_header
    if cookie:
        headers['Cookie'] = cookie
    req = urllib.request.Request(url, headers=headers, method='GET')
    try:
        with _NO_REDIRECT_OPENER.open(req, timeout=timeout) as resp:
            body = resp.read(65536).decode('utf-8', errors='replace')
            return resp.status, {k.lower(): v for k, v in resp.headers.items()}, body
    except urllib.error.HTTPError as err:
        body = err.read(65536).decode('utf-8', errors='replace') if err.fp else ''
        return err.code, {k.lower(): v for k, v in err.headers.items()}, body


def find_js_entry_ref(web_dir: Path) -> str:
    index_path = web_dir / 'index.html'
    if not index_path.is_file():
        raise RuntimeError(f'Missing deployed index.html at {index_path}')
    html = index_path.read_text(encoding='utf-8')
    m = re.search(r'(?:src|href)="(/web/assets/[^"]+\.js)"', html)
    if not m:
        raise RuntimeError('No /web/assets/*.js reference found in deployed index.html')
    return m.group(1)


def copy_tree_clean(src: Path, dst: Path) -> None:
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)


def restore_web_backup(backup_web: Path, web_dir: Path) -> None:
    if not backup_web.is_dir():
        raise RuntimeError(f'Web rollback directory missing: {backup_web}')
    copy_tree_clean(backup_web, web_dir)


def deploy_web_stage(
    web_tar: Path,
    bundle_dir: Path,
    app_dir: Path,
    web_dir: Path,
    stamp: str,
    site_host: str,
    caddyfile: Path,
    caddy_url: str,
    public_safe: bool,
) -> dict[str, object]:
    if not site_host:
        raise RuntimeError('DEPLOY_SITE_HOST is required when deploying web assets')
    rollback_parent = app_dir / f'rollback-web-{stamp}'
    rollback_parent.mkdir(parents=True, exist_ok=True)
    os.chmod(rollback_parent, 0o700)
    backup_web = rollback_parent / 'web'
    if web_dir.is_dir():
        shutil.copytree(web_dir, backup_web)

    stage_dir = web_dir.parent / f'.botagent-web-stage-{stamp}'
    try:
        if stage_dir.exists():
            shutil.rmtree(stage_dir)
        stage_dir.mkdir(parents=True, exist_ok=True)
        with tarfile.open(web_tar, 'r:gz') as tar:
            tar.extractall(stage_dir, filter='data')
        for root, dirs, files in os.walk(stage_dir):
            os.chmod(root, 0o755)
            for d in dirs:
                os.chmod(Path(root) / d, 0o755)
            for f in files:
                os.chmod(Path(root) / f, 0o644)

        copy_tree_clean(stage_dir, web_dir)

        verify_src = bundle_dir / 'verify-web-deploy.sh'
        if not verify_src.is_file() and (bundle_dir.parent / 'tools/verify-web-deploy.sh').is_file():
            verify_src = bundle_dir.parent / 'tools/verify-web-deploy.sh'
        if not verify_src.is_file() and (Path(__file__).resolve().parent / 'verify-web-deploy.sh').is_file():
            verify_src = Path(__file__).resolve().parent / 'verify-web-deploy.sh'
        verify_dst = app_dir / 'verify-web-deploy.sh'
        if verify_src.is_file():
            shutil.copyfile(verify_src, verify_dst)
            os.chmod(verify_dst, 0o755)
        if not verify_dst.is_file():
            raise RuntimeError(f'Missing verify-web-deploy.sh at {verify_dst}')

        verify_bin = os.environ.get('BOTAGENT_VERIFY_WEB_BIN', 'bash')
        verify_env = dict(os.environ, CADDYFILE=str(caddyfile), BASE=caddy_url.rstrip('/'))
        res = run_proc([verify_bin, str(verify_dst), str(web_dir), site_host], env=verify_env, timeout=60)
        if res.returncode != 0:
            raise RuntimeError(f'verify-web-deploy.sh failed (rc={res.returncode})')

        entry_js = find_js_entry_ref(web_dir)
        base = caddy_url.rstrip('/')
        unauth_code, unauth_headers, _ = http_request(base + entry_js, host_header=site_host)
        cache_ctl = unauth_headers.get('cache-control', '')
        if unauth_code != 303 or 'no-store' not in cache_ctl.lower():
            raise RuntimeError(f'Unauthenticated asset gate check failed: status={unauth_code}, cache={cache_ctl!r}')

        cookie = read_caddy_cookie(caddyfile)
        if not cookie:
            raise RuntimeError('Unable to read @authed Cookie from Caddyfile for 404 gate check')
        missing_code, _, _ = http_request(base + '/web/assets/__botagent_cd_missing__.js', host_header=site_host, cookie=cookie)
        if missing_code != 404:
            raise RuntimeError(f'Missing asset 404 gate check failed: status={missing_code}')

        log_step(f'[web] verified ({entry_js}, unauth=303 no-store, missing=404)', site_host, public_safe)
        return {
            'status': 'ok',
            'rollbackDir': str(rollback_parent),
            'entryJs': entry_js,
            'unauthStatus': unauth_code,
            'missingStatus': missing_code,
            'indexSha256': sha256_file(web_dir / 'index.html'),
        }
    except Exception:
        if backup_web.is_dir():
            restore_web_backup(backup_web, web_dir)
        raise
    finally:
        shutil.rmtree(stage_dir, ignore_errors=True)


def resolve_runtime_root(app_dir: Path, data_dir: Path) -> Path:
    if data_dir == app_dir and (app_dir / 'data').is_dir():
        return app_dir / 'data'
    return data_dir


def resolve_plugins_paths(runtime_root: Path) -> tuple[Path, str]:
    if (runtime_root / 'data/plugins').is_dir():
        return runtime_root / 'data/plugins', '/data/data/plugins'
    if (runtime_root / 'plugins').is_dir():
        return runtime_root / 'plugins', '/data/plugins'
    if (runtime_root / 'data').is_dir():
        return runtime_root / 'data/plugins', '/data/data/plugins'
    return runtime_root / 'plugins', '/data/plugins'


def find_sqlite_db(runtime_root: Path) -> Path | None:
    candidates: list[Path] = []
    for name in ('botagent.db', 'qqchat.db'):
        candidates.append(runtime_root / 'data' / name)
        candidates.append(runtime_root / name)
    for path in candidates:
        if path.is_file() and path.stat().st_size > 0:
            return path
    for path in candidates:
        if path.is_file():
            return path
    return None


SQLITE_SIDECARS = ('-wal', '-shm', '-journal')


def _copy_sqlite_with_sidecars(db_path: Path, dst_dir: Path) -> Path:
    dst_db = dst_dir / db_path.name
    shutil.copyfile(db_path, dst_db)
    os.chmod(dst_db, 0o600)
    for suffix in SQLITE_SIDECARS:
        sidecar = Path(str(db_path) + suffix)
        if sidecar.is_file():
            dst_sidecar = dst_dir / (db_path.name + suffix)
            shutil.copyfile(sidecar, dst_sidecar)
            os.chmod(dst_sidecar, 0o600)
    return dst_db


def vacuum_sqlite_snapshot(db_path: Path, snapshot_path: Path) -> int:
    if snapshot_path.exists():
        snapshot_path.unlink()
    escaped = str(snapshot_path).replace("'", "''")

    def _vacuum_via_isolated_copy() -> None:
        if snapshot_path.exists():
            snapshot_path.unlink()
        # Recover uncheckpointed WAL/journal inside disk-backed snapshot dir without touching live DB files or /tmp tmpfs.
        with tempfile.TemporaryDirectory(prefix='botagent-sqlite-snap-', dir=str(snapshot_path.parent)) as tmp_dir:
            tmp_db = _copy_sqlite_with_sidecars(db_path, Path(tmp_dir))
            rw = sqlite3.connect(str(tmp_db))
            try:
                rw.execute('PRAGMA wal_checkpoint(TRUNCATE)')
                rw.execute(f"VACUUM INTO '{escaped}'")
            finally:
                rw.close()

    has_sidecars = any(Path(str(db_path) + suffix).is_file() for suffix in SQLITE_SIDECARS)
    if has_sidecars:
        _vacuum_via_isolated_copy()
    else:
        try:
            src = sqlite3.connect(f'file:{db_path.as_posix()}?mode=ro', uri=True)
            try:
                src.execute(f"VACUUM INTO '{escaped}'")
            finally:
                src.close()
        except sqlite3.OperationalError:
            _vacuum_via_isolated_copy()
    os.chmod(snapshot_path, 0o600)
    dst = sqlite3.connect(str(snapshot_path))
    try:
        integrity = dst.execute('PRAGMA integrity_check').fetchone()
        if not integrity or integrity[0] != 'ok':
            raise RuntimeError(f'SQLite snapshot integrity_check failed: {integrity}')
        ver = dst.execute('PRAGMA user_version').fetchone()
        return int(ver[0]) if ver else 0
    finally:
        dst.close()


def archive_plugins_dir(plugins_dir: Path, tar_path: Path) -> int:
    count = 0
    with tarfile.open(tar_path, 'w') as tar:
        if plugins_dir.is_dir():
            for path in sorted(plugins_dir.rglob('*')):
                rel = path.relative_to(plugins_dir).as_posix()
                tar.add(path, arcname=rel)
                if path.name in ('manifest.json', 'installed.json'):
                    count += 1
    os.chmod(tar_path, 0o600)
    return count


def restore_backend_from_rollback(
    rollback_dir: Path,
    app_dir: Path,
    data_dir: Path,
    extra_networks: list[str],
    health_url: str,
    timeout_sec: int,
) -> dict[str, object]:
    run_proc([docker_bin(), 'stop', '--time', '30', 'qqchat-bot'], timeout=60)

    runtime_root = resolve_runtime_root(app_dir, data_dir)
    snap_db = rollback_dir / 'botagent.db'
    live_db = find_sqlite_db(runtime_root)
    if snap_db.is_file():
        default_db = (runtime_root / 'data/botagent.db') if (runtime_root / 'data').is_dir() else (runtime_root / 'botagent.db')
        target_db = live_db if live_db else default_db
        target_db.parent.mkdir(parents=True, exist_ok=True)
        for suffix in SQLITE_SIDECARS:
            sidecar = Path(str(target_db) + suffix)
            if sidecar.exists():
                sidecar.unlink()
        shutil.copyfile(snap_db, target_db)
        os.chmod(target_db, 0o600)

    plugins_tar = rollback_dir / 'plugins-before.tar'
    if plugins_tar.is_file():
        plugins_dir, _ = resolve_plugins_paths(runtime_root)
        if plugins_dir.exists():
            shutil.rmtree(plugins_dir)
        plugins_dir.mkdir(parents=True, exist_ok=True)
        with tarfile.open(plugins_tar, 'r') as tar:
            tar.extractall(plugins_dir, filter='data')

    for name in ('app.tar.gz', 'Dockerfile.app'):
        saved = rollback_dir / name
        if saved.is_file():
            shutil.copyfile(saved, app_dir / name)

    prev_tag = f'qqchat-agent:prev-release-{rollback_dir.name.removeprefix("rollback-release-")}'
    check_tag = docker(['image', 'inspect', '--format', '{{.Id}}', prev_tag], timeout=20)
    if check_tag.returncode != 0:
        raise RuntimeError(f'Missing rollback image tag {prev_tag}; refusing unsafe fallback to :prev')
    tag_source = prev_tag
    res = docker(['tag', tag_source, 'qqchat-agent:latest'], timeout=20)
    if res.returncode != 0:
        raise RuntimeError(f'Failed to re-tag rollback image {tag_source}')

    compose_base = ['compose', '-p', 'qqchat', '--project-directory', str(app_dir), '-f', str(app_dir / 'docker-compose.yml')]
    up_res = docker([*compose_base, 'up', '-d', '--no-deps', '--no-build', 'qqchat'], timeout=120)
    if up_res.returncode != 0:
        raise RuntimeError(f'Rollback docker compose up failed: {up_res.stderr}')

    current_nets = container_networks('qqchat-bot')
    for net in extra_networks:
        if net not in current_nets:
            docker(['network', 'connect', net, 'qqchat-bot'], timeout=30)

    deadline = time.monotonic() + min(timeout_sec, 90)
    while time.monotonic() < deadline:
        try:
            code, _, _ = http_request(health_url.rstrip('/') + '/healthz', timeout=5)
        except (urllib.error.URLError, TimeoutError, OSError):
            code = 0
        if code == 200:
            return {'restoredImageTag': tag_source, 'healthz': 200}
        time.sleep(2)
    raise RuntimeError('Rollback container failed /healthz check')


def count_new_log_errors(log_path: Path, offset: int) -> int:
    if not log_path.is_file():
        return 0
    size = log_path.stat().st_size
    start = offset if size >= offset else 0
    count = 0
    pat = re.compile(r'ERROR|Exception|Unhandled|Traceback')
    with log_path.open('rb') as stream:
        stream.seek(start)
        for line in stream:
            if pat.search(line.decode('utf-8', errors='replace')):
                count += 1
    return count


def prune_old_rollbacks(app_dir: Path, prefix: str, keep: int, protected: set[str]) -> None:
    if keep <= 0:
        return
    dirs = sorted(
        [p for p in app_dir.iterdir() if p.is_dir() and p.name.startswith(prefix) and p.name not in protected],
        key=lambda p: p.name,
        reverse=True,
    )
    for old in dirs[keep:]:
        shutil.rmtree(old, ignore_errors=True)


def prune_old_image_tags(app_dir: Path, keep_candidates: int, keep_rollbacks: int, protected_tags: set[str]) -> list[str]:
    res = docker(['images', 'qqchat-agent', '--format', '{{.Repository}}:{{.Tag}}'], timeout=20)
    if res.returncode != 0:
        return []
    prev_tags: set[str] = set()
    candidate_tags: set[str] = set()
    for line in res.stdout.splitlines():
        ref = line.split('\t', 1)[0].strip()
        if ':' not in ref:
            continue
        repo, tag = ref.split(':', 1)
        if repo != 'qqchat-agent':
            continue
        if tag.startswith('prev-release-'):
            prev_tags.add(ref)
        elif tag.startswith('candidate-'):
            candidate_tags.add(ref)

    to_remove: list[str] = []
    if keep_rollbacks > 0:
        surviving_prev = {
            f'qqchat-agent:prev-release-{p.name.removeprefix("rollback-release-")}'
            for p in app_dir.iterdir()
            if p.is_dir() and p.name.startswith('rollback-release-')
        }
        to_remove.extend(sorted(ref for ref in prev_tags if ref not in surviving_prev and ref not in protected_tags))
    if keep_candidates > 0:
        ordered_candidates = sorted(candidate_tags, reverse=True)
        keep_candidate_set = set(ordered_candidates[:keep_candidates]) | protected_tags
        to_remove.extend(ref for ref in ordered_candidates if ref not in keep_candidate_set)

    pruned: list[str] = []
    for old_ref in to_remove:
        rm_res = docker(['rmi', old_ref], timeout=30)
        if rm_res.returncode == 0:
            pruned.append(old_ref)
    return pruned


def wait_backend_ready(
    napcat_before: str | None,
    health_url: str,
    expected_version: str,
    timeout_sec: int,
    allow_unready: bool,
) -> dict[str, object]:
    deadline = time.monotonic() + timeout_sec
    last_reason = 'starting'
    while time.monotonic() < deadline:
        if napcat_before is not None:
            napcat_now = container_identity('qqchat-napcat')
            if napcat_now != napcat_before:
                raise RuntimeError('qqchat-napcat container identity changed unexpectedly')

        insp = docker(['inspect', '--format', '{{.State.Status}} {{.State.Health.Status}}', 'qqchat-bot'], timeout=15)
        if insp.returncode != 0:
            last_reason = 'inspect_failed'
            time.sleep(2)
            continue
        health_state = insp.stdout.strip()
        if health_state != 'running healthy':
            last_reason = f'container={health_state}'
            time.sleep(2)
            continue

        try:
            h_code, _, h_body = http_request(health_url.rstrip('/') + '/healthz', timeout=5)
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last_reason = f'healthz_err={type(exc).__name__}'
            time.sleep(2)
            continue
        if h_code != 200:
            last_reason = f'healthz={h_code}'
            time.sleep(2)
            continue
        try:
            h_json = json.loads(h_body)
        except json.JSONDecodeError:
            h_json = {}
        actual_ver = str(h_json.get('version', ''))
        if expected_version and actual_ver and actual_ver != expected_version:
            raise RuntimeError(f'Deployed version mismatch: expected {expected_version}, got {actual_ver}')

        if allow_unready:
            return {'version': actual_ver or expected_version, 'healthz': 200, 'readyz': 'skipped'}

        try:
            r_code, _, r_body = http_request(health_url.rstrip('/') + '/readyz', timeout=5)
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last_reason = f'readyz_err={type(exc).__name__}'
            time.sleep(2)
            continue
        if r_code == 200:
            try:
                r_json = json.loads(r_body)
            except json.JSONDecodeError:
                r_json = {}
            if r_json.get('ready') is True and r_json.get('onebot') == 'connected':
                return {'version': actual_ver or expected_version, 'healthz': 200, 'readyz': 200}
            last_reason = 'readyz_payload_not_connected'
        else:
            last_reason = f'readyz={r_code}'
        time.sleep(2)
    raise RuntimeError(f'Backend readiness timeout: {last_reason}')


def deploy_backend_stage(
    app_tar: Path,
    dockerfile_src: Path,
    app_dir: Path,
    data_dir: Path,
    stamp: str,
    napcat_before: str | None,
    health_url: str,
    expected_version: str,
    timeout_sec: int,
    allow_unready: bool,
    web_backup_dir: Path | None,
    web_dir: Path,
) -> dict[str, object]:
    current_image = container_image_id('qqchat-bot')
    prior_networks = container_networks('qqchat-bot')
    extra_networks = [n for n in prior_networks if n not in ('qqchat', 'qqchat_default', 'default')]

    rollback_dir = app_dir / f'rollback-release-{stamp}'
    rollback_dir.mkdir(parents=True, exist_ok=True)
    os.chmod(rollback_dir, 0o700)

    scratch_ctx = app_dir / f'.deploy-ctx-{stamp}'
    preflight_dir = app_dir / f'.deploy-preflight-{stamp}'
    scratch_ctx.mkdir(parents=True, exist_ok=True)
    preflight_dir.mkdir(parents=True, exist_ok=True)
    os.chmod(scratch_ctx, 0o700)
    os.chmod(preflight_dir, 0o700)

    candidate_tag = f'qqchat-agent:candidate-{stamp}'
    candidate_container = ''
    stopped = False
    adopted = False
    try:
        shutil.copyfile(app_tar, scratch_ctx / 'app.tar.gz')
        shutil.copyfile(dockerfile_src, scratch_ctx / 'Dockerfile.app')
        iid_file = scratch_ctx / 'image.iid'

        build_res = docker(['build', '-f', str(scratch_ctx / 'Dockerfile.app'), '-t', candidate_tag, '--iidfile', str(iid_file), str(scratch_ctx)], timeout=600)
        if build_res.returncode != 0:
            raise RuntimeError(f'Candidate docker build failed: {build_res.stderr[-400:]}')
        candidate_image = iid_file.read_text(encoding='utf-8').strip()

        create_res = docker(['create', '--network', 'none', '--entrypoint', '/bin/true', candidate_tag], timeout=30)
        if create_res.returncode != 0 or not create_res.stdout.strip():
            raise RuntimeError('Failed to create candidate inspection container')
        candidate_container = create_res.stdout.strip().splitlines()[-1]
        cp_res = docker(['cp', f'{candidate_container}:/app/BotAgent.Headless.dll', str(preflight_dir / 'candidate.dll')], timeout=30)
        if cp_res.returncode != 0:
            raise RuntimeError('Failed to extract BotAgent.Headless.dll from candidate image')

        docker(['tag', current_image, 'qqchat-agent:prev'], timeout=20)
        docker(['tag', current_image, f'qqchat-agent:prev-release-{stamp}'], timeout=20)

        runtime_root = resolve_runtime_root(app_dir, data_dir)
        plugins_dir, container_plugins_dir = resolve_plugins_paths(runtime_root)

        log_path = runtime_root / 'logs/qqchat.log'
        if not log_path.is_file() and (runtime_root / 'data/logs/qqchat.log').is_file():
            log_path = runtime_root / 'data/logs/qqchat.log'
        log_offset = log_path.stat().st_size if log_path.is_file() else 0

        stopped = True
        stop_res = docker(['stop', '--time', '30', 'qqchat-bot'], timeout=60)
        if stop_res.returncode != 0:
            raise RuntimeError('Failed to stop qqchat-bot cleanly')

        preflight_res = docker(
            [
                'run',
                '--rm',
                '--network',
                'none',
                '--read-only',
                '--cap-drop',
                'ALL',
                '--security-opt',
                'no-new-privileges',
                '--volumes-from',
                'qqchat-bot:ro',
                '--mount',
                f'type=bind,src={preflight_dir},dst=/preflight,readonly',
                '--entrypoint',
                'dotnet',
                current_image,
                '/app/BotAgent.Headless.dll',
                '--check-plugin-state',
                container_plugins_dir,
                '--candidate',
                '/preflight/candidate.dll',
            ],
            timeout=60,
        )
        if preflight_res.returncode != 0:
            docker(['start', 'qqchat-bot'], timeout=60)
            stopped = False
            if web_backup_dir and web_backup_dir.is_dir():
                restore_web_backup(web_backup_dir, web_dir)
            raise RuntimeError(f'Offline plugin-state preflight rejected candidate (rc={preflight_res.returncode})')

        live_db = find_sqlite_db(runtime_root)
        user_version = None
        if live_db is not None:
            user_version = vacuum_sqlite_snapshot(live_db, rollback_dir / 'botagent.db')

        plugin_manifests = archive_plugins_dir(plugins_dir, rollback_dir / 'plugins-before.tar')

        for item in ('app.tar.gz', 'Dockerfile.app', 'docker-compose.yml', '.env'):
            src_item = app_dir / item
            if src_item.is_file():
                dst_item = rollback_dir / item
                shutil.copyfile(src_item, dst_item)
                os.chmod(dst_item, 0o600)

        shutil.copyfile(app_tar, app_dir / 'app.tar.gz')
        shutil.copyfile(dockerfile_src, app_dir / 'Dockerfile.app')
        os.chmod(app_dir / 'app.tar.gz', 0o644)
        os.chmod(app_dir / 'Dockerfile.app', 0o644)

        docker(['tag', candidate_image, 'qqchat-agent:latest'], timeout=20)
        adopted = True
        compose_base = ['compose', '-p', 'qqchat', '--project-directory', str(app_dir), '-f', str(app_dir / 'docker-compose.yml')]
        c_up = docker([*compose_base, 'up', '-d', '--no-deps', '--no-build', 'qqchat'], timeout=120)
        if c_up.returncode != 0:
            raise RuntimeError('docker compose up qqchat failed')

        now_nets = container_networks('qqchat-bot')
        for net in extra_networks:
            if net not in now_nets:
                net_res = docker(['network', 'connect', net, 'qqchat-bot'], timeout=30)
                if net_res.returncode != 0:
                    raise RuntimeError(f'Failed to re-attach network {net}')

        ready_info = wait_backend_ready(napcat_before, health_url, expected_version, timeout_sec, allow_unready)
        running_image = container_image_id('qqchat-bot')
        if running_image != candidate_image:
            raise RuntimeError(f'Running image mismatch: expected {candidate_image}, got {running_image}')
        err_count = count_new_log_errors(log_path, log_offset)
        return {
            'status': 'ok',
            'rollbackDir': str(rollback_dir),
            'previousImage': current_image,
            'candidateImage': candidate_image,
            'runningImage': running_image,
            'sqliteUserVersion': user_version,
            'pluginManifestsBackedUp': plugin_manifests,
            'extraNetworksReattached': extra_networks,
            'newErrorLines': err_count,
            **ready_info,
        }
    except Exception:
        try:
            if adopted:
                restore_backend_from_rollback(rollback_dir, app_dir, data_dir, extra_networks, health_url, timeout_sec)
            elif stopped:
                docker(['start', 'qqchat-bot'], timeout=60)
        finally:
            if (adopted or stopped) and web_backup_dir and web_backup_dir.is_dir():
                restore_web_backup(web_backup_dir, web_dir)
        raise
    finally:
        if candidate_container:
            docker(['rm', '-f', candidate_container], timeout=20)
        shutil.rmtree(scratch_ctx, ignore_errors=True)
        shutil.rmtree(preflight_dir, ignore_errors=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)

    for cmd_name in ('deploy', 'rollback'):
        p = sub.add_parser(cmd_name)
        if cmd_name == 'deploy':
            p.add_argument('--bundle-dir', required=True)
            p.add_argument('--expected-version', default='')
            p.add_argument('--min-free-mb', type=int, default=1024)
            p.add_argument('--keep-rollbacks', type=int, default=5)
        else:
            p.add_argument('--stamp', required=True)
        p.add_argument('--app-dir', default='/opt/qqchat')
        p.add_argument('--web-dir', default='/var/www/botagent-web')
        p.add_argument('--data-dir', default='')
        p.add_argument('--scope', choices=('all', 'backend', 'web'), default='all')
        p.add_argument('--caddyfile', default='/etc/caddy/Caddyfile')
        p.add_argument('--health-url', default='http://127.0.0.1:8080')
        p.add_argument('--caddy-url', default='http://127.0.0.1:8081')
        p.add_argument('--timeout-sec', type=int, default=180)
        p.add_argument('--allow-missing-napcat', action='store_true')
        p.add_argument('--allow-unready', action='store_true')
        p.add_argument('--public-log-safe', action='store_true')
        p.add_argument('--receipt-out', default='')

    args = parser.parse_args(argv)
    app_dir = Path(args.app_dir)
    web_dir = Path(args.web_dir)
    data_dir = Path(args.data_dir) if args.data_dir else app_dir
    site_host = os.environ.get('DEPLOY_SITE_HOST', '').strip()
    public_safe = bool(args.public_log_safe)

    napcat_before = container_identity('qqchat-napcat')
    if napcat_before is None and not args.allow_missing_napcat:
        raise RuntimeError('qqchat-napcat container not found; aborting deployment')

    if args.command == 'rollback':
        stamp = args.stamp.strip()
        receipt: dict[str, object] = {'action': 'rollback', 'stamp': stamp, 'scope': args.scope}
        if args.scope in ('all', 'web'):
            backup_web = app_dir / f'rollback-web-{stamp}/web'
            restore_web_backup(backup_web, web_dir)
            receipt['web'] = {'status': 'rolled_back', 'from': str(backup_web)}
        if args.scope in ('all', 'backend'):
            rollback_dir = app_dir / f'rollback-release-{stamp}'
            if not rollback_dir.is_dir():
                raise RuntimeError(f'Missing backend rollback directory: {rollback_dir}')
            prior_nets = container_networks('qqchat-bot')
            extra_nets = [n for n in prior_nets if n not in ('qqchat', 'qqchat_default', 'default')]
            receipt['backend'] = restore_backend_from_rollback(rollback_dir, app_dir, data_dir, extra_nets, args.health_url, args.timeout_sec)
        if napcat_before is not None and container_identity('qqchat-napcat') != napcat_before:
            raise RuntimeError('qqchat-napcat identity changed during rollback')
        receipt['napcatUntouched'] = napcat_before is not None
        out_json = json.dumps(receipt, indent=2)
        if args.receipt_out:
            Path(args.receipt_out).write_text(out_json + '\n', encoding='utf-8')
        log_step(out_json, site_host, public_safe)
        return 0

    bundle_dir = Path(args.bundle_dir)
    verified_sums = verify_sha256sums(bundle_dir)
    app_tar, web_tar, dockerfile_src = resolve_bundle_assets(bundle_dir)
    if args.scope in ('all', 'backend') and (app_tar is None or dockerfile_src is None):
        raise RuntimeError('Missing linux-x64 tarball or Dockerfile.app in bundle')
    if args.scope in ('all', 'web') and web_tar is None:
        raise RuntimeError('Missing web tarball in bundle')

    check_free_space(app_dir, args.min_free_mb)
    stamp = dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    receipt = {
        'action': 'deploy',
        'stamp': stamp,
        'scope': args.scope,
        'verifiedAssets': len(verified_sums),
        'napcatUntouched': napcat_before is not None,
    }

    web_backup_dir: Path | None = None
    if args.scope in ('all', 'web') and web_tar is not None:
        web_result = deploy_web_stage(
            web_tar,
            bundle_dir,
            app_dir,
            web_dir,
            stamp,
            site_host,
            Path(args.caddyfile),
            args.caddy_url,
            public_safe,
        )
        web_backup_dir = Path(str(web_result['rollbackDir'])) / 'web'
        receipt['web'] = web_result
        prune_old_rollbacks(app_dir, 'rollback-web-', args.keep_rollbacks, {f'rollback-web-{stamp}'})

    if args.scope in ('all', 'backend') and app_tar is not None and dockerfile_src is not None:
        backend_result = deploy_backend_stage(
            app_tar,
            dockerfile_src,
            app_dir,
            data_dir,
            stamp,
            napcat_before,
            args.health_url,
            args.expected_version.strip(),
            args.timeout_sec,
            args.allow_unready,
            web_backup_dir,
            web_dir,
        )
        receipt['backend'] = backend_result
        prune_old_rollbacks(app_dir, 'rollback-release-', args.keep_rollbacks, {f'rollback-release-{stamp}'})
        pruned_tags = prune_old_image_tags(
            app_dir,
            2,
            args.keep_rollbacks,
            {f'qqchat-agent:candidate-{stamp}', f'qqchat-agent:prev-release-{stamp}'},
        )
        if isinstance(backend_result, dict):
            backend_result['prunedImageTags'] = pruned_tags

    if napcat_before is not None and container_identity('qqchat-napcat') != napcat_before:
        raise RuntimeError('qqchat-napcat identity changed after deployment')

    out_json = json.dumps(receipt, indent=2)
    if args.receipt_out:
        Path(args.receipt_out).write_text(out_json + '\n', encoding='utf-8')
    log_step(out_json, site_host, public_safe)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
