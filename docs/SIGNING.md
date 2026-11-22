# Signature de Arr411.exe

Les options pour signer `Arr411.exe`, et comment activer la signature dans la CI. Tant que rien n'est configuré, la CI ne change pas : elle publie l'exe non signé, comme aujourd'hui.

## Aujourd'hui

`Arr411.exe` n'est pas signé : SmartScreen affiche « Windows a protégé votre PC » au premier lancement. Sa provenance est en revanche attestée par GitHub à chaque release. Pour vérifier que l'exe que tu as téléchargé sort bien du workflow de ce dépôt :

```powershell
gh attestation verify .\Arr411.exe --repo HugoMeyer/arr411-desktop
```

## Les options

| Critère | SignPath Foundation | Azure Artifact Signing | Certum (certificat open source) |
|---|---|---|---|
| Coût | gratuit pour l'open source | abonnement Azure payant, sans offre gratuite ni essai | certificat payant |
| Conditions | licence OSI (MIT : ok), projet maintenu et déjà publié, MFA, réputation vérifiable : la Fondation peut refuser | un particulier n'est accepté qu'aux États-Unis et au Canada : impossible depuis la France pour l'instant | à lire chez Certum |
| Éditeur affiché par Windows | « SignPath Foundation » (le certificat est à son nom) | l'identité validée | ton nom, tel que Certum l'a validé |
| Automatique en CI | oui, action officielle | oui, `Azure/artifact-signing-action` | non : la clé est dans un HSM en ligne avec une 2FA interactive (SimplySign), donc signature à la main sur un PC |
| Approbation à la main | oui, à chaque release | non | sans objet |

**SignPath Foundation est l'option recommandée** : gratuite, automatisable, et l'approbation à la main est un garde-fou de plus. Azure n'est pas ouvert à un particulier en France ; Certum reste une solution manuelle de repli.

Sources (novembre 2026) : <https://signpath.org/terms>, <https://learn.microsoft.com/azure/artifact-signing/quickstart> et <https://learn.microsoft.com/azure/artifact-signing/faq>. Ces conditions évoluent : relis-les avant de te lancer.

## Activer la signature avec SignPath

La CI ne signe que les tags `v*`, et seulement si la variable `SIGNPATH_ORGANIZATION_ID` existe : sans elle, le job `sign` est ignoré. Avec elle, une release n'est jamais publiée sans signature : si la signature échoue, si la demande est refusée ou si le secret manque, rien n'est publié. Pour revenir aux releases non signées, supprime la variable.

1. Fais la demande sur <https://signpath.org/apply>.
2. Active l'authentification multifacteur (MFA) sur GitHub et sur SignPath.
3. Installe l'application GitHub de SignPath sur le dépôt : <https://github.com/apps/signpath>.
4. Dans SignPath, crée le projet `arr411-desktop`. Ajoute le système de build de confiance « GitHub.com » à l'organisation, puis lie-le au projet.
5. Crée la configuration d'artefact du projet (la CI n'en nomme pas : c'est la configuration par défaut qui sert). La CI envoie `Arr411.exe` seul, sans zip (`archive: false`), donc la racine est un `<pe-file>` :

   ```xml
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <pe-file product-name="Arr411 Desktop">
       <authenticode-sign/>
     </pe-file>
   </artifact-configuration>
   ```

   Syntaxe : <https://docs.signpath.io/artifact-configuration>. Les conditions de la Fondation demandent des restrictions de métadonnées sur le nom du produit (ci-dessus) **et** sur sa version (`product-version`). La version de l'exe est de la forme `0.2.1+<commit>` : le SDK ajoute le commit. Règle ce point avec SignPath avant le premier tag ; au besoin, retire le suffixe avec `IncludeSourceRevisionInInformationalVersion` à `false` dans `Directory.Build.props`.
6. Crée la politique de signature `release-signing`, avec le processus d'approbation. Si tu limites les noms de branches, ajoute aussi `v*` : un build lancé par un tag n'est pas sur `main`. Tant que ses politiques de pipeline sont actives, SignPath accepte au plus 3 ré-exécutions d'un même build : les suivantes échouent, et il faut alors un nouveau tag.
7. Crée un utilisateur CI dans SignPath, ajoute-le comme *Submitter* de la politique et copie son jeton d'API. L'identifiant de l'organisation et les slugs sont sur la page de détail de la politique.
8. Dans le dépôt, **Settings** → **Secrets and variables** → **Actions** :
   - variable `SIGNPATH_ORGANIZATION_ID` (obligatoire : c'est elle qui active la signature) ;
   - variables `SIGNPATH_PROJECT_SLUG` (défaut `arr411-desktop`) et `SIGNPATH_SIGNING_POLICY_SLUG` (défaut `release-signing`), seulement si tes slugs sont différents ;
   - secret `SIGNPATH_API_TOKEN` : le jeton de l'utilisateur CI.
9. Pousse un tag `vX.Y.Z`, puis approuve la demande dans SignPath (tu reçois un e-mail) : la CI attend une heure au plus. Si le délai est dépassé ou si tu refuses, le job `sign` échoue et **rien n'est publié**. Pour réessayer, relance seulement les jobs en échec (**Re-run failed jobs**), puis approuve la nouvelle demande. Évite **Re-run all jobs** : l'envoi d'un artefact dont le nom existe déjà dans le run risque d'échouer.

## Code signing policy

À coller dans `README.md` **une fois le projet accepté par SignPath**, et pas avant : ce bloc décrit la signature par SignPath et ne vaut que lorsqu'elle est activée. Garde le titre « Code signing policy » : SignPath l'exige sur la page d'accueil et sur les pages de release.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Rôles** : committers, reviewers et approvers : [HugoMeyer](https://github.com/HugoMeyer).
- **Confidentialité** : Arr411 ne contacte que les serveurs que tu configures (C411, qBittorrent, Radarr, Sonarr, Lidarr) et, pour les mises à jour, GitHub (`api.github.com` et ses serveurs de téléchargement). Aucune donnée personnelle n'est envoyée : la recherche de mises à jour demande seulement la dernière version publiée (GitHub voit, comme pour tout accès à son site, ton adresse IP et la version d'Arr411).
- Tu peux couper la recherche automatique de mises à jour dans **Réglages** → **Mises à jour** → **Mise à jour automatique** → **Désactivée** : GitHub n'est alors contacté que si tu cliques sur **Vérifier maintenant**.
- Le site c411.org et le WebView2 Runtime de Microsoft suivent leurs propres politiques de confidentialité.

## Vérifier un exe signé

```powershell
Get-AuthenticodeSignature .\Arr411.exe | Format-List
```

`Status` doit valoir `Valid` et `SignerCertificate` doit nommer « SignPath Foundation ». Tu peux aussi ouvrir **Propriétés** → **Signatures numériques**. La CI fait déjà cette vérification avant de publier.

## Plus tard

Le programme de mise à jour intégré vérifie aujourd'hui l'empreinte SHA-256 de l'exe (`SHA256SUMS.txt`) : c'est pourquoi la CI réécrit ce fichier pour l'exe signé. Vérifier aussi l'éditeur Authenticode lors d'une mise à jour est une étape ultérieure.
