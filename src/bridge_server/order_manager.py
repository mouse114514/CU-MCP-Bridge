from typing import Optional
from transport import HttpBridgeServer as PipeServer


class Order:
    """A macro order to be executed by the C# mod."""

    def __init__(self, action: str, parameters: dict, order_id: str = None,
                 pipeline_input: str = None, pipeline_output: str = None):
        self.id = order_id or f"ord_{id(self)}"
        self.action = action
        self.parameters = parameters
        self.pipeline_input = pipeline_input
        self.pipeline_output = pipeline_output
        self.status = "pending"
        self.result: Optional[dict] = None

    def to_dict(self) -> dict:
        d = {"id": self.id, "action": self.action, "parameters": self.parameters}
        pipeline = {}
        if self.pipeline_input:
            pipeline["input_from"] = self.pipeline_input
        if self.pipeline_output:
            pipeline["output_to"] = self.pipeline_output
        if pipeline:
            d["pipeline"] = pipeline
        return d


class OrderManager:
    """Manages order queue, pipeline parameter passing, and dispatch."""

    def __init__(self, pipe: PipeServer):
        self.pipe = pipe
        self._orders: dict[str, Order] = {}
        self._pipeline_vars: dict[str, dict] = {}

    def dispatch(self, order: Order):
        """Send an order to C# mod via pipe."""
        self._orders[order.id] = order
        msg = {"type": "order", "data": order.to_dict()}
        self.pipe.send(msg)
        order.status = "dispatched"

    def resolve_pipeline_input(self, order: Order) -> Optional[dict]:
        """If order expects pipeline input, return the stored variable."""
        if order.pipeline_input and order.pipeline_input in self._pipeline_vars:
            return self._pipeline_vars[order.pipeline_input]
        return None

    def store_pipeline_output(self, order_id: str, result: dict):
        """Store an order's output so downstream orders can use it."""
        order = self._orders.get(order_id)
        if order and order.pipeline_output:
            self._pipeline_vars[order.pipeline_output] = result
        else:
            self._pipeline_vars[order_id] = result

    def get_order(self, order_id: str) -> Optional[Order]:
        return self._orders.get(order_id)

    def get_pending_count(self) -> int:
        return sum(1 for o in self._orders.values() if o.status == "pending")

    def mark_completed(self, order_id: str, result: dict = None):
        if order_id in self._orders:
            self._orders[order_id].status = "completed"
            self._orders[order_id].result = result
            if result:
                self.store_pipeline_output(order_id, result)
