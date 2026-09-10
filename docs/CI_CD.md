# CI/CD operations

The `build-test-deploy.yml` GitHub Actions workflow validates pull requests and deploys merged code using GitHub-hosted Linux runners.

## Delivery flow

| Event | Result |
| --- | --- |
| Pull request into `develop` or `main` | Restore, release build, tests, and Docker build validation |
| Push/merge to `develop` | Repeat validation, scan and publish an immutable image, deploy to `dev` |
| Push/merge to `main` | Repeat validation, scan and publish an immutable image, deploy to `prd` |
| Manual dispatch | Repeat validation and deploy the selected `dev` or `prd` environment |

Deployments use the image digest rather than a mutable tag. GitHub Actions concurrency cancels superseded development runs and serializes production runs.

## GitHub configuration

Create GitHub Environments named `dev` and `prd`. Define these variables in each environment:

| Variable | Example | Purpose |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Application/client UUID | OIDC deployment identity |
| `AZURE_TENANT_ID` | Microsoft Entra tenant UUID | Azure login |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription UUID | Azure login |
| `ACR_LOGIN_SERVER` | `acrolgadevmalaysiaweste.azurecr.io` | Image registry |
| `AZURE_RESOURCE_GROUP` | `rg-olga-dev-malaysiawest` | Container App resource group |
| `CONTAINER_APP_NAME` | `ca-olga-core-api-dev` | Deployment target |

No long-lived Azure client secret is required. This repository uses GitHub's immutable OIDC subject format. Configure the deployment identity's federated credentials with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these exact subjects:

- `dev`: `repo:Ol-gaTechnologies@306667340/Olga.Core@1358930841:environment:dev`
- `prd`: `repo:Ol-gaTechnologies@306667340/Olga.Core@1358930841:environment:prd`

The `job_workflow_ref` claim is not the federated credential subject. Grant the deployment identity only `AcrPush` on the relevant registry and the minimum Container App update permission on the target resource group or app.

Protect `prd` with required reviewers, prevent self-review, restrict it to `main`, and disable administrator bypass. Restrict `dev` to `develop`. Keep environment variables scoped to their environment.

Protect `main` and `develop` with pull requests, at least one approval, resolved conversations, dismissal of stale approvals, no force pushes/deletions, and the required `Build and test` status check. Require branches to be current before merge. Configure a ruleset if repository administrators must also follow the policy.

## Development runtime

The `dev` deployment runs in `malaysiawest` with these provisioned resources:

| Resource | Name |
| --- | --- |
| Resource group | `rg-olga-dev-malaysiawest` |
| Container registry | `acrolgadevmalaysiaweste` |
| Container Apps environment | `cae-olga-dev-devmalaysiaweste` |
| Core API Container App | `ca-olga-core-api-dev` |
| PostgreSQL Flexible Server | `psql-olga-devmalaysiaweste.postgres.database.azure.com` |
| PostgreSQL database | `olga_connect_dev` |
| Key Vault | `kv-olga-devmalaysiaweste` |
| Runtime managed identity | `id-olga-core-dev` |

The PostgreSQL server has public network access disabled. It uses the delegated subnet `snet-postgresql` and private DNS zone `private.postgres.database.azure.com`. The Container Apps environment uses `snet-container-apps` for VNet integration.

Configure the API container with `ConnectionStrings__PostgreSql` as a Key Vault-backed Container Apps secret reference. The connection must use the server FQDN above, database `olga_connect_dev`, port `5432`, and TLS certificate verification. Configure `ServiceAuthorization__Token` as a separate Key Vault-backed secret; the application refuses to start with PostgreSQL enabled when this setting is absent. Never place either secret value in GitHub variables or workflow YAML.

The current registry uses the non-ABAC permission model. Grant the GitHub deployment identity `AcrPush` on `acrolgadevmalaysiaweste`, and grant the runtime managed identity only `AcrPull` on that registry. The runtime identity also needs permission to read the referenced Key Vault secrets. PostgreSQL schema creation and upgrades must run from a trusted host with network access to the private database endpoint.

The API image listens on port `8080`. Configure Container Apps ingress and probes for that target port. Use `/health` as the process liveness endpoint and `/ready` as the readiness endpoint; `/ready` verifies PostgreSQL connectivity. Published images use the immutable commit tag `acrolgadevmalaysiaweste.azurecr.io/olga-core-api:<commit-sha>`, and deployment resolves that image to its digest.

## Runner choice

GitHub-hosted `ubuntu-latest` runners are suitable for build, test, image publication, and Azure Container Apps deployment. Use a self-hosted runner only for private-network work such as database migrations against the private PostgreSQL endpoint. Put such a runner in a dedicated runner group, use ephemeral instances, allow only selected repositories, and never run untrusted pull-request code on it.

## Rollback

Container Apps keeps revisions, and every deployment is traceable to a commit SHA and digest. Roll back by activating the last known-good revision in Azure, then revert the offending commit so source control and runtime state converge. Do not repoint or reuse an existing image tag.
