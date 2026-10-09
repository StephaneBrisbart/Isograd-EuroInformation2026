# IA responsables : analyse et première solution

## Le problème
- On choisit des **modèles** à entraîner. Chaque modèle a une valeur, un coût en énergie et 1 à 4 besoins en données (types `n`, `t`, `i`, `c`), chacun avec un intervalle `[lowerBound, upperBound]`.
- Un besoin est rempli par des **datasets** du même type dont la somme des tailles tombe dans l'intervalle. Chaque dataset sert au plus une fois.
- Un modèle **mono-type** entraîné peut servir de **source** : il satisfait entièrement le besoin de son type chez un autre modèle (une seule cible par source, et un modèle qui a reçu une source ne peut pas être source).
- Contrainte globale : somme des énergies (cibles + sources) ≤ `energyCap`.

## Notation
- Score = somme des valeurs des modèles entraînés (les sources ne rapportent rien mais coûtent leur énergie).
- Valeur divisée par 2 (arrondi inférieur) si le modèle utilise au moins un dataset sous copyright **ou** une source. Les deux malus ne se cumulent pas.
- Solution invalide = 0 point. Classement : 1 000 000 × (votre score / meilleur score) par entrée.

## Format de sortie
```json
{ "dataMappings": [[datasetId, modelId], ...], "modelMappings": [[sourceId, targetId], ...] }
```

## Ce que révèlent les entrées
| Entrée | Modèles | Datasets | Contrainte dominante | Particularité |
|---|---|---|---|---|
| 1_example | 5 | 5 | - | exemple |
| 2_medium | 3 000 | 10 000 | énergie (cap ≈ 14 % du coût total) | 65 % des datasets sous copyright |
| 3_free | 5 000 | 20 000 | énergie (≈ 13 %) | aucun copyright |
| 4_precise | 8 000 | 40 000 | énergie (≈ 17 %) | intervalles de largeur ≈ 5, aucun modèle mono-type |
| 5_energy | 11 000 | 100 000 | données (énergie ≈ 109 % du total) | presque tout entraînable |
| 6_shortage | 15 000 | 5 000 | données (offre ≈ 3,5 % de la demande) | sources indispensables |
| 7_big | 50 000 | 250 000 | énergie (≈ 17 %) | grande taille |

Pièges repérés : des modèles avec `upperBound` négatif (impossibles), des modèles à coût énergie = 1 (quasi gratuits), des valeurs à 0, et des mono-types avec `lowerBound` = 1 (sources très bon marché en données).

## Approche v1 (IsogradIA/Program.cs)
1. Tri des modèles par `valeur / (coût/cap + λ × Σ lowerBound/offre du type)`.
2. Pour chaque modèle : on tente un remplissage 100 % libre de droits (valeur pleine), sinon mixte (copyright en priorité pour garder les libres), avec éventuellement des sources.
3. Remplissage d'un besoin : plus gros datasets d'abord, puis le plus petit dataset qui fait tomber la somme dans l'intervalle (gaspillage minimal).
4. 24 configurations (8 valeurs de λ × 3 modes de sources) testées en parallèle, la meilleure est écrite et revérifiée par un vérificateur qui reproduit `test_solution.py`.

## Scores v1 (validés par test_solution.py)
| Entrée | Score | Énergie utilisée |
|---|---|---|
| 1_example | 12 946 (optimal, fait à la main) | 99,6 % |
| 2_medium | 263 579 | 100 % |
| 3_free | 523 522 | 100 % |
| 4_precise | 849 327 | 100 % |
| 5_energy | 3 703 281 | 88 % |
| 6_shortage | 339 130 | 43 % |
| 7_big | 5 697 964 | 100 % |

## Pistes d'amélioration
- **6_shortage** : l'énergie n'est utilisée qu'à 43 %. Il faut exploiter massivement les sources (un petit dataset → une source mono-type à petit `lowerBound` → un besoin entier d'une cible).
- **Entrées limitées par l'énergie (2, 4, 7)** : réserver les datasets libres aux modèles de plus forte valeur, puis recherche locale (échanger un modèle retenu contre un modèle exclu plus rentable).
- **5_energy** : 98 % des datasets sont consommés ; réduire le gaspillage et le nombre de datasets par besoin.
- **1_example** : 12 946 est atteignable (source mono-type n → modèle 1).

## Utilisation
```
dotnet run -c Release -- <dossier datasets> --out <dossier solutions>
```
Sous Visual Studio : ouvrir `IsogradIA.sln`, et dans Propriétés > Déboguer, mettre en arguments le chemin du dossier `datasets`.
