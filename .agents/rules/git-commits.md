# Git Commits

Generate exactly one commit message for the whole staged change according to the project Git Flow.

## YouTrack Issue

When the current branch contains a valid YouTrack issue ID, associate the commit with exactly one issue.

Extract the issue ID from the current branch name when one is present.

Branch format:

`<type>/<ISSUE-ID>-<short-description>`

Example:

`feature/FW-1568-cookie-consent`

Issue ID:

`FW-1568`

Do not invent an issue ID.

If the branch does not contain a valid issue ID, use the fallback commit format below instead of asking for an issue ID.
If the staged changes belong to multiple unrelated issues, ask the user to split them before generating the commit message.

## Format

With an issue ID:

`<ISSUE-ID>: <short summary>`

Without an issue ID:

`<type>: <short summary>`

For the fallback format, use one of these types: `feat`, `fix`, `chore`, `docs`, `test`, `refactor`, `perf`, `style`, `build`, `ci`, or `revert`.

Optional body:

```text
<ISSUE-ID or type>: <short summary>

- Describe an important change.
- Explain relevant context or behavior.
- Mention limitations or compatibility concerns when necessary.
```

## Header Rules

- Generate exactly one commit message.
- Start with the YouTrack issue ID from the current branch when one exists; otherwise start with the selected commit type.
- Write the summary in English.
- Start the summary with a lowercase imperative verb: `add`, not `added`.
- Describe the main purpose of the staged change.
- Keep the complete first line at 72 characters or fewer.
- Keep the first line on one visible line.
- Do not end the first line with a period.
- For issue-based commits, do not use a Conventional Commit type in addition to the issue ID.
- For commits without an issue ID, use the fallback type list above.
- Do not add a scope.
- Do not mention every changed file.
- Do not list implementation details in the first line.
- Avoid vague summaries such as `update code`, `some changes`, or `fix issues`.

Recommended verbs:

`add`, `implement`, `update`, `fix`, `remove`, `refactor`, `rename`, `handle`, `validate`, `move`, `extract`, `replace`, `prevent`

## Body Rules

- Add a body only when the header is insufficient.
- Separate the body from the header with one blank line.
- Write the body in English.
- Prefer bullet points for multiple related changes.
- Explain what changed and why.
- Include important limitations, side effects, migrations, or compatibility notes when relevant.
- Do not repeat the header without adding useful context.

## Change Scope

- Treat the staged files as one logical change.
- Generate one message for the entire staged change.
- Do not generate separate messages per file, folder, component, workspace, or configuration.
- Do not combine multiple YouTrack issue IDs in one commit.
- Choose the summary that best represents the main purpose of the change.

## Output

Return only the commit message.

Do not include:

- explanations;
- alternatives;
- Markdown code fences;
- surrounding quotation marks.

## Examples

`FW-1568: implement cookie consent`

`FW-1602: fix cart loading state`

`FW-1620: refactor product API contracts`

`fix: handle missing issue ID`

Longer example:

```text
FW-1568: implement cookie consent

- Add the consent store and connect it to the API.
- Display the banner only when no stored decision exists.
- Handle loading and request error states.
- Prevent duplicate consent submissions.
```
