# tpcli

A session-supervised CLI for AI calling with Microsoft Teams Phone Extensibility
and Azure Voice Live.

**Status:** Initial implementation in progress. Not yet a production-ready calling tool.

The design combines a Rust CLI and session-scoped broker, a C#/.NET calling runtime,
asynchronous commands and replayable events, and SQLCipher-encrypted local history.
Call ownership is tied to a supervising terminal or agent session. No application
audio recording is planned.

See the [implementation specification](docs/SPEC.md) for requirements, architecture,
acceptance criteria, and the live-service feasibility gates.

Deployments require separately provisioned Teams and Azure resources. Never commit
credentials, tenant-specific profiles, transcripts, or private environment details.
