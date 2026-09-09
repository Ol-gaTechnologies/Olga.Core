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
| `ACR_LOGIN_SERVER` | `acrolgadev.azurecr.io` | Image registry |
| `AZURE_RESOURCE_GROUP` | `rg-olga-dev-malaysiawest` | Container App resource group |
| `CONTAINER_APP_NAME` | `ca-olga-core-api-dev` | Deployment target |

No long-lived Azure client secret is required. Add one federated credential per environment to the deployment identity with subject `repo:Ol-gaTechnologies/Olga.Core:environment:dev` or `repo:Ol-gaTechnologies/Olga.Core:environment:prd`. Grant only `AcrPush` on the relevant registry and the minimum Container App update permission on the target resource group or app.

Protect `prd` with required reviewers, prevent self-review, restrict it to `main`, and disable administrator bypass. Restrict `dev` to `develop`. Keep environment variables scoped to their environment.

Protect `main` and `develop` with pull requests, at least one approval, resolved conversations, dismissal of stale approvals, no force pushes/deletions, and the required `Build and test` status check. Require branches to be current before merge. Configure a ruleset if repository administrators must also follow the policy.

## Runner choice

GitHub-hosted `ubuntu-latest` runners are suitable for build, test, image publication, and Azure Container Apps deployment. Use a self-hosted runner only for private-network work such as database migrations against the private PostgreSQL endpoint. Put such a runner in a dedicated runner group, use ephemeral instances, allow only selected repositories, and never run untrusted pull-request code on it.

## Rollback

Container Apps keeps revisions, and every deployment is traceable to a commit SHA and digest. Roll back by activating the last known-good revision in Azure, then revert the offending commit so source control and runtime state converge. Do not repoint or reuse an existing image tag.
