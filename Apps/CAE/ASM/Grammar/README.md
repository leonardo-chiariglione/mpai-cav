# The grammar of Audio Scene Management

What a person says to ASM is data, not code and not hand-written rules.

| File | What it is |
|---|---|
| `asm-grammar.json` | the words of the verbs, the directions, the properties of the room, the amounts (the steps), the phrases of the listings |
| `asm-context.base.json` | the rest of the Application Context (MMC-APC) of CAE-ASM: conventions, answers, questions, the state, the names |
| `compile.py` | makes the Rules and the Shortcuts of `../Access/AsmApplicationContext.json` from the two |

```
python compile.py            writes ../Access/AsmApplicationContext.json (never edit that file by hand)
python compile.py --check    says whether it is what the sources give
```

A rule is a family of sentences: a verb, a target, a direction or a place, an amount - and what they come to in CAE-UCM and CAE-ACP (M3282). Moving or turning an object, or setting when it
sounds, states only the part of the Spatial Attitude that changes (the position, the orientation or the time): nothing is read of the others. A sound that waits to come in is told from one that is in
the scene by its name, and "it" is the one that waits, else the one spoken of last.

The sentences and the commands they come to are recorded in `Test/Expected/asm-grammar.json` (`AsmGrammarTests`): every command is also checked against the schema of the User Command.
After a change to the grammar: `python compile.py`, run `AsmGrammarTests`, review the differences, accept them with `MPAI_UPDATE_EXPECTED=1`.
