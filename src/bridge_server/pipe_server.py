"""Backwards-compat shim.

The named-pipe transport was replaced by a localhost HTTP transport; the code
now lives in ``transport.py``. This module is kept so older imports and helper
scripts (`from pipe_server import PipeServer`) keep working.
"""

from transport import (  # noqa: F401
    HttpBridgeServer,
    PipeServer,
    PIPE_NAME,
    SOCKET_PATH,
    DEFAULT_HOST,
    DEFAULT_PORT,
)
