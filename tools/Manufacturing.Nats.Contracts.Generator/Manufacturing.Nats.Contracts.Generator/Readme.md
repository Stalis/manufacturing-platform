# NATS contract generator

`nats.contract.yaml` is an AdditionalFile. The generator emits topology constants in `NatsContract` and sealed service clients in its `TargetNamespace` (or assembly namespace).

Every subject entry contains its literal `subject` and fully qualified C# `model`. `services.*.streams.*.subjects` entries use JetStream; `services.*.core_subjects` entries use Core NATS. Transport is determined by placement, not a repeated field. Entry keys determine method names and service containers determine client ownership; no subject parsing or separate binding table is needed.

Optional `reply_model` is supported only for Core bindings and requires an explicit `queue_group`. JetStream replies are rejected because their ReplyTo is an acknowledgement subject, not the original requester's inbox.

Generated APIs:

- `Publish{Key}Async(Model, CancellationToken)` fixes the subject and transport. JetStream publishes check acknowledgement success.
- Core bindings with replies additionally produce `Request{Key}Async` returning `ValueTask<NatsMsg<Reply>>` and `Respond{Key}Async(Func<Model, CancellationToken, Task<Reply>>, Action<Exception, string>, CancellationToken)`. Responders check deserialization errors, reject empty payloads, invoke the handler, and reply only when an inbox is present. Non-cancellation errors are reported to the supplied callback and, when an inbox is present, answered with an empty error reply carrying headers `Nats-Service-Error` (exception message) and `Nats-Service-Error-Code: 500`; a failing error reply is also reported to the callback and does not stop the loop. `Request{Key}Async` throws `NatsException` when the reply carries `Nats-Service-Error`, so callers fail fast instead of waiting for the request timeout. Core messages have no ACK/NAK or redelivery guarantee.
- `Read{DurableName}Async(CancellationToken)` fixes the stream, durable consumer and DTO type and yields `INatsJSMsg<Model>`, preserving caller-controlled ACK/NAK. Consumers matching different DTO types are rejected.

Clients use the connection's serializer registry, so applications retain their source-generated JSON context registrations. DTO existence and caller payload mismatches are checked by C# compilation against the referenced contract projects.

Build error `NATS001` (no generated source) is reported when more than one `nats.contract.yaml` is supplied, when the contract is empty or whitespace, or when it is invalid: malformed YAML, duplicate keys/subjects, missing subjects or models, invalid queue groups, JetStream reply metadata, and unsupported consumers. The test project compiles generated clients against the real NATS and contract assemblies and checks invalid contracts without pinning generated source text.
