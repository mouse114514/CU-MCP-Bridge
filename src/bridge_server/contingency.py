from transport import HttpBridgeServer as PipeServer


class ContingencyRule:
    """A condition-action pair executed locally by C# mod."""

    def __init__(self, condition: str, action: str, parameters: dict = None):
        self.condition = condition
        self.action = action
        self.parameters = parameters or {}

    def to_dict(self) -> dict:
        return {
            "condition": self.condition,
            "action": self.action,
            "parameters": self.parameters,
        }


class ContingencyManager:
    """Manages contingency rule sets sent to the C# mod."""

    def __init__(self, pipe: PipeServer):
        self.pipe = pipe

    def update_rules(self, rules: list[ContingencyRule]):
        """Send updated contingency rules to C# mod."""
        msg = {
            "type": "contingency_update",
            "data": {"rules": [r.to_dict() for r in rules]},
        }
        self.pipe.send(msg)

    def clear_rules(self):
        """Clear all contingency rules on C# side."""
        self.pipe.send({"type": "contingency_clear", "data": {}})
