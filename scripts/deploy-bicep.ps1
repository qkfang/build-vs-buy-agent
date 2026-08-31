
az group create --name rg-bvb --location eastus

az deployment group create --name "deploy-build-vs-buy" --resource-group rg-bvb --template-file "main.bicep" --parameters "main.bicepparam"
