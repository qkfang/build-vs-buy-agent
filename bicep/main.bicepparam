using 'main.bicep'

// Base name drives all resource names; keep it short.
param baseName = 'bvb'

// Region for the POC. Australia East matches the template-repo-agent reference.
param location = 'australiaeast'

// App Service plan SKU. P0v3 is a cost-effective production-class tier for a POC.
param appServiceSku = 'P0v3'

// Model deployment for the prompt agent.
param modelDeploymentName = 'gpt-5.5'
param modelName = 'gpt-5.5'
param modelVersion = '2026-04-24'
param modelCapacity = 500

param containerName = 'estimations'
