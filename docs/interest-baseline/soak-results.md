# Interest management soak baseline

Written by `InterestSoakTests` (`dotnet test --filter TestCategory=Soak` in `Services~`). Each
row is one world shape of design §11 at one size, with the same local density. The point of the
table is the *pairs*: within a shape, replicas, bytes/s, cache and links must stay flat as the
world grows tenfold. Absolute numbers depend on the machine; the ratios do not.

| Shape | World entities | Replicas | Bytes/s to client | Gateway cache | Worker links | Eval ms | Gaps | Duplicates | Leaks |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| zones (3 workers) | 2000 | 9 | 11030 | 13 | 3 | 0.05 | 0 | 0 | 0 |
| zones (3 workers) | 20000 | 9 | 10544 | 13 | 3 | 0.03 | 0 | 0 | 0 |
| grid (4 workers) | 2000 | 9 | 13079 | 13 | 4 | 0.08 | 0 | 0 | 0 |
| grid (4 workers) | 20000 | 9 | 6460 | 13 | 4 | 0.51 | 0 | 0 | 0 |
| one container | 2000 | 9 | 10463 | 13 | 1 | 0.04 | 0 | 0 | 0 |
| one container | 20000 | 9 | 10463 | 13 | 1 | 0.03 | 0 | 0 | 0 |
