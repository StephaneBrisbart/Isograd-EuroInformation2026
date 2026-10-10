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

## Approche (v4)
1. **Relaxation linéaire** (OR-Tools GLOP) : pour chaque modèle, valeur pleine, valeur/2 ou source ; contraintes d'énergie, de volume libre et total par type, et de jetons source par type. Elle donne une borne supérieure (très proche des meilleurs scores du classement), les modèles à entraîner et les prix duaux des ressources.
2. **Appariement global datasets → sources** : chaque source retenue par le LP reçoit à l'avance un seul dataset dont la taille tombe dans son intervalle (sources triées par borne sup, plus petit dataset suffisant). Le volume gaspillé sur 6_shortage passe de 490 k à 288 k.
3. **Construction gloutonne** guidée par le LP : valeur pleine avec datasets libres, sinon copyright d'abord, sources pour les besoins que le LP couvre par jeton. Fin de remplissage par un dataset ou une paire avec un surplus toléré réglable.
4. **Besoins impossibles** (borne sup négative) : couverts par une source, ce que le vérificateur accepte.
5. Mode `--improve` : repart de la solution écrite et alterne réemballage global des datasets (valeurs pleines, puis sources, puis le reste) et recherche locale.
6. Prix lagrangiens par sous-gradient comme alternative, grille de paramètres, recherche aléatoire, puis recherche locale « détruire / reconstruire » jusqu'à la limite (`--time`). Une solution n'est écrite que si elle bat celle déjà présente.

## Scores (validés par test_solution.py, 2026-10-09)
| Entrée | Score | Borne LP | Meilleur du classement |
|---|---|---|---|
| 1_example | 12 946 | - | 12 946 |
| 2_medium | 272 035 | 272 491 | 272 308 |
| 3_free | 523 698 | 523 761 | 523 758 |
| 4_precise | 1 031 623 | 1 031 992 | 1 031 935 |
| 5_energy | 3 779 262 | 3 788 025 | 3 785 898 |
| 6_shortage | 666 781 | 671 259 | 669 603 |
| 7_big | 5 820 962 | 5 824 402 | 5 822 471 |

1_example a été résolu à la main (source 0 vers le modèle 1). Le LP de 7_big prend environ 3 min.

## Pistes restantes
- 6_shortage : il reste environ 1,1 % de volume gaspillé ; choisir les sources en fonction des petits datasets disponibles (et pas seulement celles du LP).

## Utilisation
```
dotnet run -c Release -- <dossier datasets> --out <dossier solutions> --time 150   (--no-lp pour désactiver le LP)
```
Sous Visual Studio : ouvrir `IsogradIA.sln`, et dans Propriétés > Déboguer, mettre en arguments le chemin du dossier `datasets`.
