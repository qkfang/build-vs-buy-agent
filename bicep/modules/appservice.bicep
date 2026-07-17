@description('Azure location')
param location string

@description('Web App name')
param webAppName string

@description('App Service plan name')
param appServicePlanName string

@description('SKU for the App Service plan')
param appServiceSku string = 'P0v3'

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('Foundry project endpoint (AI Foundry API)')
param foundryProjectEndpoint string

@description('Entra tenant ID used to authenticate to the Foundry project')
param foundryTenantId string = tenant().tenantId

@description('Model deployment name used by the prompt agent')
param foundryModelDeploymentName string

@description('Enable the live Foundry agent path (false => deterministic offline engine)')
param foundryEnabled bool = true

@description('Storage account name for uploads / generated workbooks')
param storageAccountName string

@description('Storage container name')
param storageContainerName string

@description('Local data folder for durable job persistence on App Service')
param localDataFolder string = '/home/site/data'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: appServiceSku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appCommandLine: 'dotnet Proj37.CostEstimator.Web.dll'
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'Foundry__Enabled'
          value: string(foundryEnabled)
        }
        {
          name: 'Foundry__ProjectEndpoint'
          value: foundryProjectEndpoint
        }
        {
          name: 'Foundry__TenantId'
          value: foundryTenantId
        }
        {
          name: 'Foundry__ModelDeploymentName'
          value: foundryModelDeploymentName
        }
        {
          name: 'Foundry__AgentName'
          value: 'proj37-cost-estimator'
        }
        {
          name: 'Foundry__UseFileSearch'
          value: 'true'
        }
        {
          name: 'Storage__AccountUrl'
          value: 'https://${storageAccountName}.blob.${environment().suffixes.storage}'
        }
        {
          name: 'Storage__ContainerName'
          value: storageContainerName
        }
        {
          name: 'Storage__LocalDataFolder'
          value: localDataFolder
        }
      ]
    }
  }
}

output webAppName string = webApp.name
output defaultHostName string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
