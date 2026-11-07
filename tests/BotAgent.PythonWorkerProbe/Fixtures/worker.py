"""Synthetic worker only: all fault modes and values are test-owned."""
import json
import os
import sys
import time
from pathlib import Path
from botagent_worker import Plugin

plugin = Plugin()


@plugin.on_load
def load(context):
    if context.configuration.get("loadMarker"):
        Path(context.configuration["loadMarker"]).write_text("loaded", encoding="utf-8")
    if context.configuration.get("loadDelay"):
        time.sleep(context.configuration["loadDelay"])
    print("synthetic startup diagnostic")


@plugin.on_unload
def unload(context):
    if context.configuration.get("unloadMarker"):
        Path(context.configuration["unloadMarker"]).write_text("unloaded", encoding="utf-8")
    if context.configuration.get("unloadDelay"):
        time.sleep(context.configuration["unloadDelay"])


@plugin.tool(name="echo", description="Synthetic bounded worker fixture", read_only=True,
             input_schema={"type": "object", "properties": {"mode": {"type": "string"}}})
def echo(arguments, context):
    mode = arguments.get("mode", "echo")
    if mode == "crash":
        os._exit(17)
    if mode == "sleep":
        if context.configuration.get("callMarker"):
            Path(context.configuration["callMarker"]).write_text(str(os.getpid()), encoding="utf-8")
        time.sleep(30)
    if mode == "malformed":
        os.write(1, b"not-json\n")
        time.sleep(30)
    if mode == "wrong_id":
        os.write(1, b'{"jsonrpc":"2.0","id":999,"result":{"content":"wrong"}}\n')
        time.sleep(30)
    if mode == "duplicate":
        response = b'{"jsonrpc":"2.0","id":3,"result":{"content":"duplicate"}}\n'
        os.write(1, response + response)
        time.sleep(30)
    if mode == "missing_is_error":
        os.write(1, b'{"jsonrpc":"2.0","id":3,"result":{"content":"missing-required-flag"}}\n')
        time.sleep(30)
    if mode == "frame":
        os.write(1, b"x" * (600 * 1024))
        time.sleep(30)
    if mode == "large":
        return "x" * 65536
    if mode == "error":
        raise ValueError("synthetic-secret-never-log")
    if mode == "denied":
        context.require_capability("network.outbound")
    if mode in ("noise", "stderr_flood"):
        repetitions = 100 if mode == "noise" else 100000
        sys.stderr.write("synthetic-secret-never-log" * repetitions)
        sys.stderr.flush()
        if mode == "stderr_flood":
            time.sleep(30)
    if mode == "env":
        return {"present": "BOTAGENT_WORKER_SYNTHETIC_SECRET" in os.environ}
    if mode == "context":
        return {"configuration": dict(context.configuration), "contextRepr": repr(context),
                "capabilities": context.capabilities}
    if mode == "count":
        marker = Path(context.configuration["countMarker"])
        count = int(marker.read_text() if marker.exists() else "0") + 1
        marker.write_text(str(count))
        os._exit(18)
    return {"value": arguments.get("value", "ok"), "pid": os.getpid()}


@plugin.tool(name="hidden", description="Not host-authorized", input_schema={"type": "object"})
def hidden(arguments, context):
    return "must not be callable"


original_dispatch = plugin._dispatch


def faulty_dispatch(method, parameters):
    result = original_dispatch(method, parameters)
    case = plugin._context.configuration.get("protocolCase")
    if method == "initialize" and case == "version":
        return {"protocolVersion": 2}
    if method == "tools/list":
        if case == "duplicates":
            result["tools"].append(result["tools"][0])
        elif case == "schema":
            result["tools"][0]["inputSchema"] = {"type": "array"}
        elif case == "many":
            result["tools"] = result["tools"] * 40
        elif case == "name":
            result["tools"][0]["name"] = "invalid.tool"
    return result


plugin._dispatch = faulty_dispatch
if __name__ == "__main__":
    plugin.run()
