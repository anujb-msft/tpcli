using Azure.Core;
using Azure.Identity;

namespace Tpcli.Azure;

internal sealed class AzureCredentials(AzureOptions options)
{
    private TokenCredential? _credential;

    internal TokenCredential Get()
    {
        AzureValidation.Identity(options.Identity);
        return _credential ??= Create(options.Identity);
    }

    private static TokenCredential Create(AzureIdentityOptions options)
    {
        if (options.Mode == "developer_azure_cli")
        {
            var cli = new AzureCliCredentialOptions { TenantId = options.DeveloperTenantId };
            cli.Diagnostics.IsLoggingEnabled = false;
            cli.Diagnostics.IsLoggingContentEnabled = false;
            cli.Diagnostics.IsDistributedTracingEnabled = false;
            return new AzureCliCredential(cli);
        }

        // Unlike the unrestricted DefaultAzureCredential chain, this cannot fall
        // through to a developer login or a credential found on the worker.
        var identity = options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId)
            : ManagedIdentityId.SystemAssigned;
        var managed = new ManagedIdentityCredentialOptions(identity);
        managed.Diagnostics.IsLoggingEnabled = false;
        managed.Diagnostics.IsLoggingContentEnabled = false;
        managed.Diagnostics.IsDistributedTracingEnabled = false;
        return new ManagedIdentityCredential(managed);
    }
}
