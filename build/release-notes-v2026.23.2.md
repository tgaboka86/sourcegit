Updates to the tib8/sourcegit fork since v2026.23.1.

## Changes

- Add a Working Directory Head dropdown in Preferences / Appearance: Hide, Show when there are local changes, and Show always. Hide is the default and restores the original graph layout.
- Use a pencil for modified-file counts, green + counts for additions, and red - counts for removals; zero counts are hidden.
- Fix the Hg terminology tooltip namespace and Appearance checkbox layout.
- Keep untranslated settings readable through English fallback resources, while preserving the selected language's translations.
- Include upstream improvements for detached HEAD display, copying branch names by hotkey, deleting an attached worktree with its branch, and updated translations.

## Release packaging

macOS packaging now uses Intel runners for both target architectures to avoid the hosted ARM runner capacity failure encountered with the previous release. The release is published after its downloads have uploaded.

## Downloads

Windows x64/ARM64, macOS Intel/Apple Silicon, and Linux x64/ARM64. This is an unofficial fork release.
