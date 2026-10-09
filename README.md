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

## Approche (v2)
1. **Prix lagrangiens** : un sous-gradient multiplicatif fixe un prix pour l'énergie, les données libres, les données totales (par type) et un "jeton source" par type. Chaque modèle choisit seul entre rien, valeur pleine, valeur/2 ou source ; les prix s'ajustent jusqu'à respecter les capacités.
2. **Construction gloutonne** dans l'ordre des profits réduits : remplissage 100 % libre pour viser la valeur pleine, sinon copyright d'abord, et sources quand un jeton coûte moins cher que les données.
3. **Remplissage d'un besoin** : gros datasets d'abord, puis on termine par un ou deux datasets qui tombent pile dans l'intervalle (indispensable pour 4_precise).
4. **Besoins impossibles** (borne sup négative) : couverts par une source, ce que le vérificateur accepte.
5. Grille de paramètres puis recherche aléatoire autour du meilleur réglage jusqu'à la limite de temps (`--time`). Une solution n'est écrite que si elle bat celle déjà présente.

## Scores (validés par test_solution.py, 2026-10-09)
| Entrée | Score | Meilleur du classement |
|---|---|---|
| 1_example | 12 946 | 12 946 |
| 2_medium | 271 544 | 272 308 |
| 3_free | 523 684 | 523 758 |
| 4_precise | 1 030 782 | 1 031 935 |
| 5_energy | 3 761 990 | 3 785 898 |
| 6_shortage | 659 450 | 669 603 |
| 7_big | 5 820 862 | 5 822 471 |

1_example a été résolu à la main (source 0 vers le modèle 1).

## Pistes restantes
- 6_shortage et 5_energy : meilleure affectation des petits datasets aux sources (appariement), recherche locale.
- Recherche locale générale : échanger un modèle retenu contre un exclu.

## Utilisation
```
dotnet run -c Release -- <dossier datasets> --out <dossier solutions> --time 60
```
Sous Visual Studio : ouvrir `IsogradIA.sln`, et dans Propriétés > Déboguer, mettre en arguments le chemin du dossier `datasets`.
