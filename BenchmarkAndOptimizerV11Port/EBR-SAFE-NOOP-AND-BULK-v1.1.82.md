# Verified EBR no-op guard / bulk compatibility — v1.1.82

## Why bulk instantiation was disabled

The previous hard-coded guard required the exact return type `List<object>`,
three arguments (`Blueprint`, `BaseComponent`, `ImmutableArray<...>`),
and `InstantiateComponent` to return `object`. Its one-line diagnostic
did not distinguish the failed condition.

Now the guard accepts a *declared return type assignable from List<object>*
(such as `IReadOnlyList<object>`) but preserves the existing exact argument
layout; it checks the complete `InstantiateComponent` signature as well.
Incompatible concrete list, array, immutable, or parameter types still use
vanilla. On failure the startup log prints both actual method signatures and
each compatibility result. This is not an unrestricted override of a game API
that may have changed semantics.

External Harmony prefixes/transpilers/finalizers on the batch method still
disable the fast path. First-use blueprint-spec resolution and all live
non-spec component construction still follow vanilla.

## EBR SetAccesses safety

The prior v1.1.81 measurements found SetAccesses responsible for around
80% of sampled EBR callback time. Identical access positions alone do not
prove that suppressing the setter preserves observer notifications, enabled
state or another mod's callbacks.

This build adds a deliberately strict verified no-op guard. It only installs
when the *actual game* SetAccesses implementation is proved to be an exact
two-field assignment from the input arguments, with **no other opcodes**,
and no external patch owners. It checks the actual current backing-field
values before eliding a repeated identical assignment. If the IL is anything
else (including any method calls), **the guard remains OFF** and all original
SetAccesses calls run. There is no assumption about the current version's
unavailable original setter body.

We expect this strict guard may stay inactive in v1.1: the per-call times are
more consistent with nontrivial setter work than trivial field stores. That
is intentional, since the original setter's side effects have not been
verified. Do not equate an installed diagnostic hook with authorization to
skip gameplay work.

## Runtime validation

- Look for `EBR identical-access fast path installed` or
  `EBR identical-access fast path inactive`.
- Look for `bulk instantiation fast path installed`, or the new detailed
  `incompatible runtime signature` report.
- Confirm `EBR.PROFILE` and normal construction/demolition continue.
- A run with identical-access guard inactive has unchanged SetAccesses
  behavior; in that case we need IL/source verification before more
  aggressive duplication removal.
- Saves, Soil Contamination, global ticks, EBR deferral budgets and the
  threading model are unchanged.
