"""Synthetic stdlib-only marketplace example; run only after trusted review."""
from botagent_worker import Plugin

plugin = Plugin()

@plugin.tool(
    name="echo",
    description="Return synthetic text unchanged; no network or filesystem access requested.",
    input_schema={"type": "object", "properties": {"text": {"type": "string", "maxLength": 200}},
                  "required": ["text"], "additionalProperties": False},
    read_only=True,
)
def echo(arguments, context):
    text = arguments.get("text")
    if not isinstance(text, str) or len(text) > 200:
        raise ValueError("Expected bounded text")
    return {"text": text, "prefix": context.configuration.get("prefix", "synthetic")}

@plugin.on_load
def load(context):
    # Configuration stays in process; do not print it or write it to logs.
    pass

@plugin.on_unload
def unload(context):
    pass

if __name__ == "__main__":
    plugin.run()
