# Ruleset de la branche `main`

`main-protection.json` décrit la protection de `main` : **pull request obligatoire**, **1 approbation** (celle du
propriétaire, voir `../CODEOWNERS`), anciennes approbations annulées à chaque nouveau commit, suppression et
réécriture de l'historique interdites.

Les administrateurs du dépôt peuvent contourner la règle **uniquement en passant par une pull request** (`bypass_mode: pull_request`) :
le propriétaire peut ainsi fusionner ses propres pull requests sans pouvoir approuver lui-même.

## Activer le ruleset

1. Sur GitHub : **Settings → Rules → Rulesets → New ruleset → Import a ruleset**.
2. Choisir `main-protection.json`, vérifier l'aperçu, puis **Create**.

Les rulesets ne s'appliquent aux dépôts **privés** que sur une offre payante (Pro, Team) ; sur l'offre gratuite ils
fonctionnent sur un dépôt **public**.
