# Adding a language

How to ship a new UI locale in Unison. Copy **English** (`en-US`) as the source of truth, then register the BCP-47 tag in the package, the project, and the `AppLanguage` enum.

Missing a registration step is why a language “exists as a folder” but never appears in Settings, or why Windows 10 Mobile only installs OS + `pt-BR`.

## What is already shipped

| Enum | BCP-47 folder | Combo label |
|---|---|---|
| `System` (−1) | *(no folder — follows OS)* | localized `Settings_LanguageSystem` |
| `English` | `en-US` | English |
| `PortugueseBrazil` | `pt-BR` | Português (Brasil) |
| `Spanish` | `es-ES` | Español |
| `Italian` | `it-IT` | Italiano |
| `Dutch` | `nl-NL` | Nederlands |
| `Indonesian` | `id-ID` | Bahasa Indonesia |
| `Polish` | `pl-PL` | Polski |
| `Ukrainian` | `uk-UA` | Українська |

`System` is not a `.resw`. It clears `PrimaryLanguageOverride` so MRT uses the OS list, then the first **shipped** match, then English.

Worked example below: **French** as `fr-FR` / `AppLanguage.French = 7`.

---

## 1. Create the resource folder (BCP-47)

UWP qualifies resources by **folder name**, not by a property inside the file.

1. Copy the whole English file (keep the ResX header and every `<data name="…">` key):

   ```
   src/Unison.Uwp/Strings/en-US/Resources.resw
     → src/Unison.Uwp/Strings/fr-FR/Resources.resw
   ```

2. Folder name = **BCP-47** tag (`language`-`REGION`): `fr-FR`, `de-DE`, `ja-JP`. Same tag everywhere (enum, csproj, manifest).
3. Translate only `<value>…</value>`. **Do not rename keys.** `Settings_Language.Header` must stay `Settings_Language.Header`.
4. Keep `xml:space="preserve"` on entries that need it (line breaks in app-bar labels).
5. Leave brand names as-is (`Unison`, `WhatsApp`, `Baileys`) unless the locale has an established form.

Include the new `.resw` in the UWP project as `PRIResource` (same ItemGroup as the others):

```xml
<PRIResource Include="Strings\fr-FR\Resources.resw" />
```

Visual Studio often adds this when you “Add → Existing Item”. If the file is on disk but not in the `.csproj`, it will not ship.

### Key shapes

| How UI uses it | `.resw` `name` | Code / XAML |
|---|---|---|
| `x:Uid="Settings_Title"` on a `TextBlock` | `Settings_Title.Text` | MRT sets `Text` |
| `SettingBox LocalizationUid="Settings_Language"` | `Settings_Language.Header` and `Settings_Language.Text` | `SettingBox` loads `{uid}/Header` and `{uid}/Text` on Loaded |
| `SettingsSectionHeader LocalizationUid="Settings_General"` | `Settings_General.Text` | `{uid}/Text` |
| ViewModel / `IStringResources.Get` | `Settings_DisconnectTitle` (no `.Property`) | `Get("Settings_DisconnectTitle", "Disconnect?")` |

`LocalizedStrings` maps dots to slashes: `Foo.Text` in resw is requested as `Foo/Text`.

XAML `Text="…"` next to `x:Uid` is the **designer / missing-key fallback**. Keep it **English** (`DefaultLanguage` is `en-US`). If the key is missing in every pack, users see that fallback — that is how “Idioma” leaked into every language.

When you add a **new** string to the app, add it to **every** existing `Resources.resw`, not only English. English-only is acceptable only if `Get(key, englishFallback)` is used in code; `x:Uid` / `SettingBox` have no C# fallback.

---

## 2. Register in the app package (do not skip)

Languages must live in the **main** package. Do **not** let MakeAppx split them into resource packs (`AppxBundleAutoResourcePackageQualifiers` must **not** include `Language`). On Windows 10 Mobile sideload, a split bundle only installs packs that match the device language; `PrimaryLanguageOverride` then falls back to `en-US` and the new locale never appears.

### `Unison.Uwp.csproj`

`DefaultLanguage` stays `en-US`. Append the new tag to `AppxDefaultResourceQualifiers` (semicolons, same order you like in the combo is fine):

```xml
<AppxDefaultResourceQualifiers>Language=en-US;pt-BR;es-ES;nl-NL;id-ID;it-IT;pl-PL;fr-FR|Scale=200</AppxDefaultResourceQualifiers>
```

Leave `|Scale=200` as-is (splash/logos).

### `Package.appxmanifest`

Under `<Resources>`, add a line for the same tag:

```xml
<Resources>
  <Resource Language="en-US" />
  <Resource Language="pt-BR" />
  <!-- …existing… -->
  <Resource Language="fr-FR" />
</Resources>
```

The list in the manifest and the list in `AppxDefaultResourceQualifiers` must match.

`Unison.Background` has no own `.resw`. Toasts use the **app package PRI** (`BackgroundStrings` → `ResourceLoader.GetForViewIndependentUse`). Translating `Toast_*` in the UWP `Resources.resw` is enough. Do not change Background `DefaultLanguage` as part of adding a UI locale.

---

## 3. Register in the enum and `AppLanguageInfo`

Persisted value is `(int)AppLanguage` in `LocalSettingsConstants.SelectedLanguage`. **Never reorder or renumber** existing members (installed devices already stored `6` = Polish). Append the next integer.

### `Unison.Core/Models/AppLanguage.cs`

```csharp
Polish = 6,
French = 7
```

### `Unison.Core/Helpers/AppLanguageInfo.cs`

Update **all four** places:

1. `AllLanguages` — after the other shipped values (`System` stays first).
2. `ShippedLanguages` — same, without `System`.
3. `GetDisplayName` — native autonym (`"Français"`), not a translation of the current UI language.
4. `GetTag` — `"fr-FR"` (same as the folder).

`TryMapShipped` already matches full tags and primary subtags (`fr` → `fr-FR` if that is the only `fr-*` you ship). If you later add `fr-CA` as well, prefer exact tags in Settings and be careful with the primary-subtag fallback.

No extra ComboBox XAML. `LanguageOptions` is built from `AppLanguageInfo.GetDisplayNames`.

`Settings_LanguageSystem` (“System” / “Sistema” / …) is already per-pack; you only translate it inside the new `Resources.resw`.

---

## 4. How language is applied at runtime

`App` constructor calls `IAppLanguageService.ApplyFromSettings()` **before** `InitializeComponent`, so `x:Uid` on the first frame is already in the right language.

Changing the ComboBox persists the enum, sets `ApplicationLanguages.PrimaryLanguageOverride`, then restarts (`AppLanguageService.ChangeLanguageAndRestartAsync`). After override changes, `LocalizedStrings.Reset()` drops the cached `ResourceLoader`.

You do not wire the new language into Boot/Settings by hand.

---

## 5. Checklist

- [ ] `Strings/{tag}/Resources.resw` copied from `en-US`, values translated, **keys unchanged**
- [ ] `<PRIResource Include="Strings\{tag}\Resources.resw" />` in `Unison.Uwp.csproj`
- [ ] `{tag}` appended to `AppxDefaultResourceQualifiers` (`Language=…;{tag}|Scale=200`)
- [ ] `<Resource Language="{tag}" />` in `Package.appxmanifest`
- [ ] `AppLanguage` member **appended** (new int, no reshuffle)
- [ ] `AppLanguageInfo`: `AllLanguages`, `ShippedLanguages`, `GetDisplayName`, `GetTag`
- [ ] New UI strings added to **every** existing pack (or English `Get` fallback in code)
- [ ] `AppxBundleAutoResourcePackageQualifiers` still **excludes** `Language`
- [ ] Rebuild, sideload, pick the language in Settings, confirm restart + UI + a background toast if you translated `Toast_*`

## 6. Do not

- Create `Strings/French/` or `fr/` — MRT wants `fr-FR`.
- Split languages into resource packs.
- Renumber `AppLanguage` values.
- Hardcode UI copy in a ViewModel or leave a Portuguese/English-only XAML fallback for a key that has no resw entry.
- Add a second `Resources.resw` under Background “for toasts”.

## Related

- [UI and shell](UI-and-Shell) — selectors and shipped list
- [Coding standards](Coding-Standards) — i18n rules for everyday changes
