"""Package this example with the canonical botagent_worker.py SDK at its root."""
from botagent_worker import Plugin

plugin = Plugin()


@plugin.tool(
    name="echo",
    description="Echo synthetic input without external I/O",
    input_schema={"type": "object", "properties": {"text": {"type": "string"}},
                  "required": ["text"], "additionalProperties": False},
    read_only=True,
)
def echo(arguments, context):
    return {"text": arguments["text"]}


if __name__ == "__main__":
    plugin.run()
