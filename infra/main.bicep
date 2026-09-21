targetScope = 'resourceGroup'

@description('Existing Container Apps environment; this template does not provision networks or shared dependencies.')
param environmentName string

param location string = resourceGroup().location
param runtimeName string = 'tpcli-runtime'
param watchdogName string = 'tpcli-watchdog'

@description('Operator-built images in an existing authenticated registry. This repository does not publish images.')
param runtimeImage string
param watchdogImage string
param registryServer string

@description('Existing user-assigned identity with approved image-pull, Key Vault and Azure service permissions.')
param identityResourceId string

@description('Nonsecret runtime configuration: array of {name, value} or {name, secretRef}. Never put secret values here.')
param runtimeEnvironment array

@description('Nonsecret watchdog configuration. It must point at the same external durable control store as the runtime.')
param watchdogEnvironment array

@description('Existing Key Vault secret references, each {name, url}. No secret values or new credentials are accepted.')
param keyVaultSecrets array = []

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

var secrets = [for secret in keyVaultSecrets: {
  name: secret.name
  keyVaultUrl: secret.url
  identity: identityResourceId
}]

resource runtime 'Microsoft.App/containerApps@2024-03-01' = {
  name: runtimeName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: secrets
      registries: [
        {
          server: registryServer
          identity: identityResourceId
        }
      ]
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      terminationGracePeriodSeconds: 30
      containers: [
        {
          name: 'runtime'
          image: runtimeImage
          env: concat([
            { name: 'Tpcli__Mode', value: 'azure' }
            { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          ], runtimeEnvironment)
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

resource watchdog 'Microsoft.App/containerApps@2024-03-01' = {
  name: watchdogName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: secrets
      registries: [
        {
          server: registryServer
          identity: identityResourceId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'watchdog'
          image: watchdogImage
          env: concat([{ name: 'Tpcli__Mode', value: 'azure' }], watchdogEnvironment)
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output runtimeOrigin string = 'https://${runtime.properties.configuration.ingress.fqdn}'
