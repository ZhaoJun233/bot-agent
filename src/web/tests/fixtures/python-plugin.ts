import type { ZbaPluginPackageManifest } from '../../src/api/zba'
export const pythonManifest: ZbaPluginPackageManifest = {
  id: 'community.example.python-echo', version: '1.0.0', apiVersion: 2,
  minimumHostVersion: '1.0.0', sha256: 'a'.repeat(64),
  downloadUrl: 'https://github.com/example/market/releases/download/community.example.python-echo-1.0.0/community.example.python-echo-1.0.0.zip',
  permissions: [], dependencies: [], settings: [],
  runtime: { kind: 'python', protocolVersion: 1, entryPoint: 'worker.py', minimumPythonVersion: '3.11', dependencyMode: 'stdlib-only', tools: ['echo'] }
}
