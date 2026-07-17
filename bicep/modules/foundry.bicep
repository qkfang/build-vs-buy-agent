@description('Azure location')
param location string

@description('AI Services account name (serves as the Foundry resource)')
param foundryServicesName string

@description('Foundry project name')
param foundryProjectName string

@description('Model deployment name used by the cost-estimator prompt agent')
param modelDeploymentName string = 'gpt-4o'

@description('Model name to deploy')
param modelName string = 'gpt-4o'

@description('Model version to deploy')
param modelVersion string = '2024-11-20'

@description('Model deployment capacity (thousands of tokens per minute)')
param modelCapacity int = 50

// Azure AI Services account with Foundry project management enabled.
resource foundrySvc 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' = {
  name: foundryServicesName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  properties: {
    allowProjectManagement: true
    customSubDomainName: foundryServicesName
    publicNetworkAccess: 'Enabled'
    // Keyless: callers (the web app's managed identity) authenticate with Entra ID.
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// Foundry project (child of the AI Services account).
resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundrySvc
  name: foundryProjectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

// Model deployment used by the prompt agent.
resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundrySvc
  name: modelDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    raiPolicyName: 'Microsoft.DefaultV2'
  }
}

output foundryAccountName string = foundrySvc.name
output aiProjectEndpoint string = aiProject.properties.endpoints['AI Foundry API']
output aiServicesEndpoint string = foundrySvc.properties.endpoint
output modelDeploymentName string = modelDeployment.name
