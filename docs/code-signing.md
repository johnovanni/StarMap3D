# Code signing for Windows releases

The release workflow (`.github/workflows/release.yml`) signs the app's .exe and its own DLLs with
**Azure Artifact Signing** (formerly Trusted Signing) on every version tag. Until the repository
variables below are set, the signing steps are skipped and releases stay unsigned, so nothing breaks.

## What signing does and doesn't do

- The SmartScreen dialog names the publisher (John Apolloni) instead of "Unknown publisher", and
  Windows can tell the file wasn't tampered with.
- It does **not** remove the "Windows protected your PC" warning on day one. No certificate does
  that any more (EV lost its instant pass in 2024). SmartScreen builds reputation for the signing
  identity as people download and run signed files, and the warning fades once it has enough.
  Every app shares the same identity, so reputation earned by one helps the others.

## One-time setup (about an hour of clicking, then a few business days of ID checks)

Cost: Basic plan, US$9.99 a month, 5,000 signatures. Billing starts when the signing account is created.

1. **Azure subscription.** Sign in at https://portal.azure.com and create a *Pay-As-You-Go*
   subscription (credit card). Free-trial and sponsored subscriptions don't work.
2. **Register the provider.** Subscriptions > your subscription > Resource providers >
   `Microsoft.CodeSigning` > Register.
3. **Create the signing account.** Search the portal for *Artifact Signing* > Create. Pick a resource
   group (new: `codesigning`), a name (for example `qapow`), region **East US**, plan **Basic**.
   The account page shows its URI: for East US it is `https://eus.codesigning.azure.net/`.
4. **Verify your identity.** On the signing account: Access control (IAM) > Add role assignment >
   *Artifact Signing Identity Verifier* > yourself. Then Objects > Identity validations > New >
   **Public** > **Individual**. Enter your legal name and address exactly as on your ID, then finish the
   ID check (photo ID through Microsoft's verification partner). Allow a few business days.
5. **Create the certificate profile.** Once the validation shows *Completed*: Objects > Certificate
   profiles > Create > **Public Trust**, name `qapow`, pick the validation from step 4.
6. **Let GitHub sign without a password.** Microsoft Entra ID > App registrations > New registration,
   name `github-signing`. Note its *Application (client) ID* and *Directory (tenant) ID*.
   In the app: Certificates & secrets > Federated credentials > Add credential > *GitHub Actions
   deploying Azure resources*. Organization `johnovanni`, repository (one at a time), entity type
   **Environment**, environment name **signing**. Repeat for each repository:
   PlatoEmpire, AlexDash, EMFieldVisualizer, GravityCylinders, StarMap3D, VoiceCoach, Journal.
7. **Give that app permission to sign.** Back on the signing account: Access control (IAM) >
   Add role assignment > *Artifact Signing Certificate Profile Signer* > select `github-signing`.
8. **Set the repository variables.** These are IDs and names, not secrets. With the GitHub CLI
   (`gh auth login` first), fill in the four values and run:

   ```sh
   CLIENT_ID=...        # step 6, Application (client) ID
   TENANT_ID=...        # step 6, Directory (tenant) ID
   SUBSCRIPTION_ID=...  # step 1
   for repo in PlatoEmpire AlexDash EMFieldVisualizer GravityCylinders StarMap3D VoiceCoach Journal; do
     gh variable set AZURE_CLIENT_ID       -R johnovanni/$repo -b "$CLIENT_ID"
     gh variable set AZURE_TENANT_ID       -R johnovanni/$repo -b "$TENANT_ID"
     gh variable set AZURE_SUBSCRIPTION_ID -R johnovanni/$repo -b "$SUBSCRIPTION_ID"
     gh variable set SIGNING_ENDPOINT      -R johnovanni/$repo -b "https://eus.codesigning.azure.net/"
     gh variable set SIGNING_ACCOUNT       -R johnovanni/$repo -b "qapow"
     gh variable set SIGNING_PROFILE       -R johnovanni/$repo -b "qapow"
   done
   ```

   (Or in each repo: Settings > Secrets and variables > Actions > Variables.)
9. **Release.** Push a new tag (for example `v1.0.1`). The run's *Check signature* step prints each
   signed .exe with its signer. On a PC, right-click the .exe > Properties > Digital Signatures.

## J. M. Apollo apps later

These apps are signed as **John Apolloni** (individual). Company apps should be signed as
**J. M. Apollo LLC**, which needs an *Organization* identity validation: a D-U-N-S number (free from
Dun & Bradstreet, can take a few weeks), a working company website and company email addresses, and
the LLC's name and address exactly as on the Minnesota record. That identity needs its own certificate
profile; add a second Basic signing account or move to Premium when the first company app is ready.
