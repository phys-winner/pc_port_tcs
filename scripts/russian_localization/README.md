# Russian localization sources

The authored translations and scene-owner patch metadata are in `data/`. These
compact inputs replace the private audit inventories for routine source builds;
the original complete game language database is not distributed.

```powershell
python -X utf8 -B scripts/russian_localization/generate_ui_data.py
dotnet run --project scripts/russian_localization/policy_test/policy_test.csproj -c Release
```

The generator writes the ignored `RussianUiData.cs` table and a local provenance
report under `work/russian_localization/`. It does not modify game assets.

For an already-patched firstpass, compile only the helper into a new directory:

```powershell
& scripts/russian_localization/build_locale.ps1 -HelperOnly -Output work/locale-next
```

This needs the matching game/Unity references and the pinned Russian font helper,
as described in [BUILDING.md](../../BUILDING.md). Without `-HelperOnly`, the
firstpass patcher requires its clean preimage and rejects existing hooks.
Canonical assemblies are not replaced automatically.

`prepare_russian_i2.py` reads the original I2 object from your own PC
`resources.assets` (`--original-data` selects its directory). It verifies the exact
source hash, combines it with `data/translations_ru.json` and
`data/supplemental_terms.json`, and prepares guarded work-only candidates. The
original language columns and offline setting are preserved. Applying candidates
is a separate explicit operation in `apply_russian_i2.py`.

`prepare_reset_label.py` is a later data-only patch for the exact Russian reset
caption. Its `--self-check` and `--verify-only` paths write nothing. Its guarded
`--apply` workflow and explicit canonical permission flag remain separate from
source generation.

The visible attribution is **ChatGPT (OpenAI)**. Generate the table before running
the policy fixture so its generated-caption assertions are included.
