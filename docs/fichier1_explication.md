# Fichier 1 (1_example) : explication de la solution

Solution soumise (`solutions/1_example_submission.json`) :

```json
{"dataMappings":[[0,0],[1,1],[2,1],[4,4]],"modelMappings":[[0,1]]}
```

Score : **12 946**, le maximum possible pour cette entrée.

## Les données

Budget d'énergie : **184 958**.

| Modèle | Valeur | Énergie | Besoins |
|---|---|---|---|
| 0 | 6 770 | 70 153 | `n` entre 1 462 et 1 749 |
| 1 | 16 940 | 88 492 | `t` entre 1 888 et 2 330 **et** `n` entre 1 892 et 2 003 |
| 2 | 9 615 | 48 961 | `t` entre 1 973 et 2 500 |
| 3 | 3 416 | 68 214 | `n` entre 1 520 et 1 810 |
| 4 | 4 476 | 25 558 | `i` entre 670 et 995 |

| Dataset | Type | Taille | Copyright |
|---|---|---|---|
| 0 | `n` | 1 524 | oui |
| 1 | `t` | 867 | non |
| 2 | `t` | 1 149 | oui |
| 3 | `i` | 740 | oui |
| 4 | `i` | 918 | non |

## Le problème du modèle 1

Le modèle 1 est le plus précieux (16 940). Son besoin `n` demande au moins 1 892, mais le seul dataset `n` fait 1 524 : trop petit, et un dataset ne sert qu'une fois. On ne peut donc pas nourrir directement son besoin `n`.

## L'astuce : un modèle source

Le modèle 0 n'a qu'un seul type de besoin (`n`). Une fois entraîné, il peut servir de **source** : il couvre à lui seul le besoin `n` d'un autre modèle, quelle que soit la quantité demandée.

1. Le dataset 0 (1 524) entraîne le modèle 0 (1 524 est bien entre 1 462 et 1 749). C'est `[0,0]`.
2. Le modèle 0 devient la source du modèle 1. C'est `modelMappings [0,1]`.
3. Le besoin `t` du modèle 1 est rempli par les datasets 1 et 2 : 867 + 1 149 = 2 016, entre 1 888 et 2 330. Ce sont `[1,1]` et `[2,1]`.
4. Le modèle 4 reçoit le dataset 4 (918, entre 670 et 995). C'est `[4,4]`.

## Le calcul du score

- Le modèle 0 sert de source : il ne rapporte rien, mais il consomme son énergie.
- Le modèle 1 utilise une source et un dataset sous copyright, donc sa valeur est divisée par 2 (une seule fois, les deux malus ne se cumulent pas) : 16 940 / 2 = **8 470**.
- Le modèle 4 n'utilise qu'un dataset libre, il garde donc sa valeur pleine : **4 476**.
- Total : 8 470 + 4 476 = **12 946**.
- Énergie : 70 153 + 88 492 + 25 558 = 184 203, sous le plafond de 184 958.

## Pourquoi on ne peut pas faire mieux

- Il ne reste que 755 d'énergie, ce qui ne suffit pour aucun autre modèle.
- La meilleure alternative sans le modèle 1 entraîne les modèles 2, 4 et 0 : 4 807 + 4 476 + 3 385 = 12 668, donc moins.
- Utiliser le modèle 3 comme source à la place du modèle 0 donne le même score de 12 946.

## Ce que ça dit des gros fichiers

Les autres entrées reposent sur le même arbitrage, à grande échelle :
- choisir quels modèles entraîner sous le budget d'énergie ;
- nourrir les uns avec des datasets libres (valeur pleine) ;
- passer les autres en copyright ou en source (valeur divisée par 2) ;
- décider quels petits modèles mono-type sacrifier comme sources.
