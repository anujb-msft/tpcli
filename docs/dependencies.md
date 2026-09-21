# Dependency and distribution notes

No repository license has been selected by the owner. The source project is not
declared MIT/Apache licensed, and build/test automation does not publish packages,
containers or releases. Rust packages have `publish = false`.

The Rust build links SQLCipher through `rusqlite` /
`libsqlite3-sys`'s `bundled-sqlcipher-vendored-openssl` feature, not the system
SQLite library. SQLCipher Community Edition and its underlying SQLite retain
their own terms; the bundled OpenSSL source and all Rust crates retain their
license notices. Cargo.lock pins the resolved dependency graph.

The .NET adapters use official Azure SDK packages and ASP.NET Core. NuGet package
versions/lock files pin the compiled service surface. .NET, Azure SDK, database
drivers and test framework packages retain their own licenses.

Before distributing binary artifacts, the owner must select the repository's
license, review the locked dependency licenses and notices, include the required
third-party notices/source offers if applicable, and review current service terms.
Do not infer licensing or support for a service combination from the SDK compiling.
