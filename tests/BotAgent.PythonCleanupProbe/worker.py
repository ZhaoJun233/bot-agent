"""Synthetic ownership fixture. No network, secrets, or real application data."""
import json
import os
import subprocess
import sys
import time


def reply(request_id, result):
    print(json.dumps({"jsonrpc": "2.0", "id": request_id, "result": result}), flush=True)


for line in sys.stdin:
    request = json.loads(line)
    request_id = request["id"]
    method = request["method"]
    if method == "initialize":
        reply(request_id, {"protocolVersion": 1})
    elif method == "tools/list":
        reply(request_id, {"tools": [{"name": "echo", "description": "Synthetic cleanup tool",
                                       "inputSchema": {"type": "object"}}]})
    elif method == "tools/call":
        args = request["params"]["arguments"]
        mode = args.get("mode", "echo")
        if mode.startswith("child_"):
            child = subprocess.Popen([sys.executable, "-I", "-c", "import time; time.sleep(120)"],
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            with open(args["pidFile"], "w", encoding="ascii") as output:
                output.write(str(child.pid))
            if mode == "child_crash":
                os._exit(23)
            if mode == "child_timeout":
                time.sleep(120)
            reply(request_id, {"content": "spawned", "isError": False})
        elif mode.startswith("error_"):
            errors = {"error_scalar": "bad", "error_missing_code": {"message": "synthetic"},
                      "error_wrong_code": {"code": "bad", "message": "synthetic"},
                      "error_wrong_message": {"code": -32000, "message": 1},
                      "error_valid": {"code": -32000, "message": "synthetic"}}
            print(json.dumps({"jsonrpc": "2.0", "id": request_id, "error": errors[mode]}), flush=True)
        elif mode == "stderr_overflow":
            sys.stderr.buffer.write(b"x" * (64 * 1024 + 1))
            sys.stderr.buffer.flush()
            time.sleep(120)
        else:
            reply(request_id, {"content": "synthetic", "isError": False})
    elif method == "shutdown":
        reply(request_id, {})
        break
