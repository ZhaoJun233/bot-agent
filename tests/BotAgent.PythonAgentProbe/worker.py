from pathlib import Path
from botagent_worker import Plugin

plugin = Plugin()

@plugin.tool(name="MixedEcho", description="Synthetic side effect counter", input_schema={"type": "object", "properties": {}, "additionalProperties": False})
def echo(arguments, context):
    marker = Path(context.configuration["marker"])
    with marker.open("a", encoding="utf-8") as output:
        output.write("effect\n")
    return {"ok": True}

plugin.run()
