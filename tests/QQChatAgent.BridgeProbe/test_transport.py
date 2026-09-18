"""Loopback-only tests; importing the bridge does not run pi or SSH."""
import importlib.util
import pathlib
import socket
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('bridge', pathlib.Path(__file__).resolve().parents[2] / 'tools' / 'pi-bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class TransportTests(unittest.TestCase):
    def test_handshake_timeout_closes_socket(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen(1)
        done = threading.Event()
        peer_closed = []

        def server():
            with listener:
                peer, _ = listener.accept()
                with peer:
                    peer.settimeout(2)
                    peer.recv(8192)
                    peer_closed.append(peer.recv(1) == b'')
            done.set()

        thread = threading.Thread(target=server, daemon=True)
        thread.start()
        with self.assertRaises(TimeoutError):
            bridge.WsClient('127.0.0.1', listener.getsockname()[1], '/synthetic', timeout=0.1)
        self.assertTrue(done.wait(2))
        self.assertEqual(peer_closed, [True])
        thread.join(2)

    def test_upgrade_and_first_frame_in_one_packet(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen(1)
        ready = threading.Event()

        def server():
            with listener:
                peer, _ = listener.accept()
                with peer:
                    peer.recv(8192)
                    peer.sendall(b'HTTP/1.1 101 Switching Protocols\r\n\r\n\x81\x02{}')
                    ready.wait(2)

        thread = threading.Thread(target=server, daemon=True)
        thread.start()
        client = bridge.WsClient('127.0.0.1', listener.getsockname()[1], '/synthetic', timeout=0.2)
        try:
            self.assertEqual(client.recv(timeout=0.2), (1, b'{}'))
            self.assertIsNotNone(client.sock.gettimeout())
        finally:
            client.close()
            ready.set()
            thread.join(2)

    def test_abort_does_not_latch_cancel(self):
        """断连时只能 abort：cancelled 粘住的话，重连后第一个任务一上来就被当成“已取消”。"""
        runner = bridge.TaskRunner(send_json=lambda obj: None, pi_argv=['synthetic-pi'], workdir='.')
        killed = []

        class FakeProc:
            def poll(self):
                return None

        runner.proc = FakeProc()
        with patch.object(bridge, '_kill_tree', lambda p: killed.append(p)):
            runner.abort()
        self.assertEqual(len(killed), 1)
        self.assertFalse(runner.cancelled)

        with patch.object(bridge, '_kill_tree', lambda p: killed.append(p)):
            runner.cancel()
        self.assertTrue(runner.cancelled)

    def test_disconnect_swaps_out_send_json(self):
        """断连后任务线程不能再往死 socket 上写（否则线程里抛 OSError）。"""
        source = pathlib.Path(bridge.__file__).read_text(encoding='utf-8')
        self.assertIn('runner.send_json = lambda obj: None', source)
        self.assertIn('runner.abort()', source)

    def test_hello_failure_retries_and_closes(self):
        instances = []

        class FakeWs:
            def __init__(self, *args):
                self.closed = False
                instances.append(self)
            def send_json(self, payload):
                raise ConnectionError('synthetic hello failure')
            def close(self):
                self.closed = True

        class FakeTunnel:
            enabled = False
            def __init__(self, *args, **kwargs): pass
            def ensure(self): pass
            def stop(self): pass

        class StopRetry(Exception): pass
        with patch.object(bridge.sys, 'argv', ['bridge', '--url', 'ws://127.0.0.1/synthetic', '--token', 'synthetic']), \
             patch.object(bridge, 'WsClient', FakeWs), patch.object(bridge, 'SshTunnel', FakeTunnel), \
             patch.object(bridge, 'resolve_pi_command', return_value=(['synthetic-pi'], 'mock')), \
             patch.object(bridge, '_pi_version', return_value='mock'), patch.object(bridge, '_list_models', return_value=[]), \
             patch.object(bridge, 'log'), patch.object(bridge.time, 'sleep', side_effect=StopRetry):
            with self.assertRaises(StopRetry):
                bridge.main()
        self.assertEqual(len(instances), 1)
        self.assertTrue(instances[0].closed)


if __name__ == '__main__':
    unittest.main()
