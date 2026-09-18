# Startup Flow

1. Build `IUcoPlugin` instance (scan assemblies for tools/prompts/resources)
2. Resolve or start the Node bridge, then connect the WebSocket hub client
3. Register tools/prompts/resources with the bridge; readiness is signalled to REST callers
