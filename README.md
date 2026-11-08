# BuilderEngine

A game engine where you build your own **mechanisms** — reusable pieces for anything (behaviors, entities, systems,
lights, editor tools) — with blocks or with **Builder Script**, a tiny language of 9 keywords. The more mechanisms you
make, the faster you make games.

Status: early prototype (v0.1.0). See [docs/MVP.md](docs/MVP.md).

```
mechanism Coin
    param value: number = 1

    when touched by "player"
        score += value
        play "coin.wav"
        destroy self
```

## Build and run

```bash
dotnet build
dotnet run --project src/BuilderEngine              # editor
dotnet run --project tests/BuilderEngine.Tests      # language/runtime tests
```

| Project | What |
|---|---|
| `src/BuilderEngine.Script` | the language and runtime (lexer, parser, interpreter with `wait` coroutines, starter kit) — no UI, reusable by any host |
| `src/BuilderEngine` | the editor (Avalonia: Windows now, Mac/Linux later) |
| `tests/BuilderEngine.Tests` | test runner, no NuGet packages |
