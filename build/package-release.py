#!/usr/bin/env python3
"""Deterministic release packager for BotAgent (linux-x64 + web + verification metadata)."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET


SENSITIVE_ENV_PREFIXES = ('BOTAGENT_', 'QQCHAT_', 'OPENAI_', 'MINIMAX_', 'TTS_', 'HARNESS_')


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def sanitized_env() -> dict[str, str]:
    return {
        k: v
        for k, v in os.environ.items()
        if not k.upper().startswith(SENSITIVE_ENV_PREFIXES) and k.upper() != 'HEALTH_PORT'
    }


def run_cmd(command: list[str], cwd: Path, env: dict[str, str]) -> str:
    proc = subprocess.run(
        command,
        cwd=str(cwd),
        env=env,
        capture_output=True,
        text=True,
        encoding='utf-8',
        errors='replace',
    )
    if proc.returncode != 0:
        detail = ((proc.stdout or '') + '\n' + (proc.stderr or '')).strip()
        raise RuntimeError(f'Command failed ({proc.returncode}): {" ".join(command)}\n{detail[-1500:]}')
    return (proc.stdout or '').strip()


def read_host_version(props_path: Path) -> str:
    if not props_path.is_file():
        raise RuntimeError(f'Missing Directory.Build.props at {props_path}')
    versions = [
        (node.text or '').strip()
        for node in ET.parse(props_path).iter('Version')
        if (node.text or '').strip()
    ]
    if len(versions) != 1:
        raise RuntimeError(f'Expected exactly 1 <Version> in Directory.Build.props, got {versions}')
    return versions[0]


def extract_changelog_notes(changelog_path: Path, version: str) -> str:
    text = changelog_path.read_text(encoding='utf-8')
    marker = f'## [{version}]'
    start = text.find(marker)
    if start < 0:
        raise RuntimeError(f'CHANGELOG.md is missing section {marker}')
    end = text.find('\n## [', start + len(marker))
    section = text[start : end if end != -1 else None].strip()
    if not section:
        raise RuntimeError(f'Empty CHANGELOG.md section for {marker}')
    return section + '\n'


def validate_web_dist(dist_dir: Path) -> None:
    index_path = dist_dir / 'index.html'
    if not index_path.is_file():
        raise RuntimeError(f'Missing web index.html at {index_path}')
    html = index_path.read_text(encoding='utf-8')
    refs = re.findall(r'(?:src|href)="(/[^"]*)"', html)
    if not refs:
        raise RuntimeError('index.html contains no root-relative asset references')
    bad = [r for r in refs if not r.startswith('/web/')]
    if bad:
        raise RuntimeError(f'Web index.html references escape /web/ prefix: {bad[:5]}')


def validate_output_tree(folder: Path, *, forbid_wwwroot: bool = False) -> list[Path]:
    if not folder.is_dir():
        raise RuntimeError(f'Output folder does not exist: {folder}')
    if forbid_wwwroot and (folder / 'wwwroot').exists():
        raise RuntimeError('Unexpected wwwroot directory in Headless publish output')
    entries = sorted(folder.rglob('*'), key=lambda p: p.relative_to(folder).as_posix())
    for entry in entries:
        if entry.is_symlink():
            raise RuntimeError(f'Symlink is forbidden in release output: {entry}')
    files = [p for p in entries if p.is_file()]
    if not files:
        raise RuntimeError(f'Release output directory is empty: {folder}')
    debug_files = [p.name for p in files if p.suffix.lower() in ('.pdb', '.map')]
    if debug_files:
        raise RuntimeError(f'Unexpected debug artifacts in release output: {debug_files[:5]}')
    return files


def create_deterministic_tar_gz(folder: Path, archive_path: Path, epoch: int, *, exe_name: str | None) -> tuple[int, dict[str, str]]:
    files = validate_output_tree(folder)
    inventory: dict[str, str] = {}
    with archive_path.open('wb') as raw:
        with gzip.GzipFile(filename='', fileobj=raw, mode='wb', mtime=epoch) as gz:
            with tarfile.open(fileobj=gz, mode='w', format=tarfile.PAX_FORMAT) as tar:
                for path in files:
                    rel = path.relative_to(folder).as_posix()
                    info = tar.gettarinfo(str(path), arcname=rel)
                    info.uid = 0
                    info.gid = 0
                    info.uname = ''
                    info.gname = ''
                    info.mtime = epoch
                    info.mode = 0o755 if (exe_name and rel == exe_name) else 0o644
                    with path.open('rb') as content:
                        tar.addfile(info, content)
                    inventory[rel] = sha256_file(path)

    # Read-back verification of every archive member.
    with tarfile.open(archive_path, 'r:gz') as tar:
        members = tar.getmembers()
        if len(members) != len(files) or {m.name for m in members} != set(inventory):
            raise RuntimeError(f'Archive member mismatch in {archive_path.name}')
        for member in members:
            if not member.isfile():
                raise RuntimeError(f'Non-file archive member in {archive_path.name}: {member.name}')
            extracted = tar.extractfile(member)
            if extracted is None:
                raise RuntimeError(f'Cannot read archive member {member.name}')
            with extracted:
                h = hashlib.sha256()
                for chunk in iter(lambda: extracted.read(1024 * 1024), b''):
                    h.update(chunk)
                if h.hexdigest() != inventory[member.name]:
                    raise RuntimeError(f'Archive digest mismatch for {member.name}')
    return len(files), inventory


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', default='.', help='Repository root path')
    parser.add_argument('--output', required=True, help='Output directory for release bundle')
    parser.add_argument('--commit', default='', help='Source commit SHA (defaults to git HEAD)')
    parser.add_argument('--tree', default='', help='Source tree SHA (defaults to git HEAD^{tree})')
    parser.add_argument('--epoch', type=int, default=-1, help='Deterministic mtime epoch (defaults to commit timestamp)')
    parser.add_argument('--ref', default='', help='Git ref (defaults to GITHUB_REF or current branch)')
    parser.add_argument('--repo', default='ZhaoJun233/bot-agent', help='GitHub repository slug')
    parser.add_argument('--skip-npm-ci', action='store_true', help='Reuse existing src/web/node_modules if present')
    parser.add_argument('--prebuilt-app', default='', help='Use prebuilt linux-x64 app directory (for offline tests)')
    parser.add_argument('--prebuilt-web', default='', help='Use prebuilt web dist directory (for offline tests)')
    args = parser.parse_args(argv)

    source = Path(args.source).resolve()
    output = Path(args.output).resolve()
    assets = output / 'assets'
    assets.mkdir(parents=True, exist_ok=True)
    env = sanitized_env()

    version = read_host_version(source / 'Directory.Build.props')
    commit = args.commit.strip() or run_cmd(['git', 'rev-parse', 'HEAD'], source, env)
    tree = args.tree.strip() or run_cmd(['git', 'rev-parse', 'HEAD^{tree}'], source, env)
    epoch = args.epoch if args.epoch >= 0 else int(run_cmd(['git', 'show', '-s', '--format=%ct', 'HEAD'], source, env))

    ref = args.ref.strip() or os.environ.get('GITHUB_REF', '').strip()
    if not ref:
        try:
            ref = run_cmd(['git', 'symbolic-ref', '-q', 'HEAD'], source, env)
        except RuntimeError:
            ref = 'HEAD'

    is_tag = ref.startswith('refs/tags/')
    if is_tag:
        tag = ref.removeprefix('refs/tags/')
        if tag != f'v{version}':
            raise RuntimeError(f'Tag {tag} does not match Directory.Build.props <Version> {version}')
        label = tag
        source_branch = 'main'
        notes = extract_changelog_notes(source / 'CHANGELOG.md', version)
    else:
        label = f'v{version}-g{commit[:8]}'
        source_branch = ref.removeprefix('refs/heads/') or 'HEAD'
        notes = f'## Candidate Build {label}\n\n- Commit: `{commit}`\n- Branch: `{source_branch}`\n'

    (output / 'RELEASE-NOTES.md').write_text(notes, encoding='utf-8')

    with tempfile.TemporaryDirectory(prefix='botagent-pkg-') as tmp_root_str:
        tmp_root = Path(tmp_root_str)
        app_dir = tmp_root / 'app'
        if args.prebuilt_app:
            shutil.copytree(Path(args.prebuilt_app).resolve(), app_dir)
        else:
            run_cmd(
                [
                    'dotnet',
                    'publish',
                    'src/BotAgent.Headless/BotAgent.Headless.csproj',
                    '-c',
                    'Release',
                    '-r',
                    'linux-x64',
                    '--self-contained',
                    'false',
                    '-o',
                    str(app_dir),
                    '-p:ContinuousIntegrationBuild=true',
                    '-p:DebugType=None',
                    '-p:DebugSymbols=false',
                    f'-p:SourceRevisionId={commit}',
                    f'-p:PathMap={source.as_posix()}=/_/src',
                ],
                source,
                env,
            )
            cli_ver = run_cmd(['dotnet', str(app_dir / 'BotAgent.Headless.dll'), '--version'], source, env)
            if cli_ver != f'BotAgent.Headless {version}':
                raise RuntimeError(f'Published Headless --version mismatch: {cli_ver!r} != {version!r}')

        if (app_dir / 'deployment.json').exists():
            raise RuntimeError('Unexpected pre-existing deployment.json in publish output')
        validate_output_tree(app_dir, forbid_wwwroot=True)
        if not (app_dir / 'BotAgent.Headless.dll').is_file():
            raise RuntimeError('Missing BotAgent.Headless.dll in publish output')

        (app_dir / 'deployment.json').write_text(
            json.dumps(
                {
                    'sourceCommit': commit,
                    'publicCommit': commit,
                    'tree': tree,
                    'version': version,
                    'rid': 'linux-x64',
                    'tag': label,
                },
                indent=2,
            )
            + '\n',
            encoding='utf-8',
        )

        web_dist = tmp_root / 'web'
        if args.prebuilt_web:
            shutil.copytree(Path(args.prebuilt_web).resolve(), web_dist)
        else:
            web_dir = source / 'src/web'
            npm_bin = 'npm.cmd' if os.name == 'nt' else 'npm'
            if not (args.skip_npm_ci and (web_dir / 'node_modules').is_dir()):
                run_cmd([npm_bin, 'ci', '--no-audit', '--no-fund'], web_dir, env)
            run_cmd([npm_bin, 'run', 'build'], web_dir, env)
            shutil.copytree(web_dir / 'dist', web_dist)

        validate_web_dist(web_dist)
        validate_output_tree(web_dist)

        packages: list[dict[str, object]] = []
        inventory: dict[str, dict[str, str]] = {}
        for suffix, folder, exe in [('linux-x64', app_dir, 'BotAgent.Headless'), ('web', web_dist, None)]:
            name = f'botagent-{label}-{suffix}.tar.gz'
            archive_path = assets / name
            count, files_map = create_deterministic_tar_gz(folder, archive_path, epoch, exe_name=exe)
            inventory[suffix] = files_map
            packages.append(
                {
                    'name': name,
                    'sha256': sha256_file(archive_path),
                    'bytes': archive_path.stat().st_size,
                    'fileCount': count,
                }
            )

        dockerfile_src = source / 'Dockerfile.app'
        if not dockerfile_src.is_file():
            raise RuntimeError(f'Missing Dockerfile.app at {dockerfile_src}')
        shutil.copyfile(dockerfile_src, assets / 'Dockerfile.app.txt')

        template_path = source / 'build/release-install.template.md'
        if not template_path.is_file():
            raise RuntimeError(f'Missing install template at {template_path}')
        install_text = (
            template_path.read_text(encoding='utf-8')
            .replace('{{VERSION}}', version)
            .replace('{{TAG}}', label)
            .replace('{{REPO}}', args.repo)
        )
        (assets / 'INSTALL.md').write_text(install_text, encoding='utf-8')

        dotnet_sdk = 'prebuilt' if args.prebuilt_app else run_cmd(['dotnet', '--version'], source, env)
        node_ver = 'prebuilt' if args.prebuilt_web else run_cmd(['node', '--version'], source, env).removeprefix('v')
        build_info = {
            'version': version,
            'sourceCommit': commit,
            'sourceTree': tree,
            'sourceRepository': f'https://github.com/{args.repo}',
            'sourceBranch': source_branch,
            'tag': label,
            'runtimeIdentifier': 'linux-x64',
            'selfContained': False,
            'requiredRuntime': '.NET 10',
            'dotnetSdk': dotnet_sdk,
            'nodeVersion': node_ver,
            'headlessDllSha256': sha256_file(app_dir / 'BotAgent.Headless.dll'),
            'webIndexSha256': sha256_file(web_dist / 'index.html'),
            'packages': packages,
        }
        (assets / 'BUILD-INFO.json').write_text(json.dumps(build_info, indent=2) + '\n', encoding='utf-8')

    checksum_lines = [
        f'{sha256_file(path)}  {path.name}\n'
        for path in sorted(assets.iterdir(), key=lambda p: p.name)
        if path.is_file() and path.name != 'SHA256SUMS.txt'
    ]
    (assets / 'SHA256SUMS.txt').write_text(''.join(checksum_lines), encoding='utf-8')

    receipt = {
        'label': label,
        'version': version,
        'isTag': is_tag,
        'sourceCommit': commit,
        'sourceTree': tree,
        'epoch': epoch,
        'assets': {
            path.name: sha256_file(path)
            for path in sorted(assets.iterdir(), key=lambda p: p.name)
            if path.is_file()
        },
        'packageFiles': inventory,
    }
    (output / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'ok': True, 'label': label, 'version': version, 'assets': len(receipt['assets'])}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
