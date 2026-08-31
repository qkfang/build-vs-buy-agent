using 'main.bicep'

// Base name drives all resource names; keep it short.
param baseName = 'bvb'

// Region for the POC. Australia East matches the template-repo-agent reference.
param location = 'australiaeast'

// App Service plan SKU. P0v3 is a cost-effective production-class tier for a POC.
param appServiceSku = 'P0v3'

// Set to true to provision + use the live Foundry prompt agent. The app also works with this false,
// using its deterministic offline estimation engine (handy if model quota is constrained).
param foundryEnabled = true

// Model deployment for the prompt agent. Use a smaller model when gpt-4o quota is exhausted.
param modelDeploymentName = 'gpt-4o-mini'
param modelName = 'gpt-4o-mini'
param modelVersion = '2024-07-18'

param containerName = 'estimations'
