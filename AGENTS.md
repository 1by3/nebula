# Repository instructions

## Documentation changes

Before you create or edit user-facing documentation, read and follow [`website/CONTENT_GUIDE.md`](website/CONTENT_GUIDE.md).

This requirement applies to:

- Pages and landing-page copy under `website/`.
- CLI command summaries, details, options, and examples that generate `website/content/docs/cli/`.
- Public C# XML comments that generate `website/content/docs/reference/`.
- Dashboard text, screenshots, and other assets published by the documentation site.

Do not edit files under `website/content/docs/cli/` or `website/content/docs/reference/` by hand. Update their CLI or C# sources, then run `npm run gen` from `website/`.

After a documentation change, run the checks listed in the content guide. At minimum, regenerate affected references, run `npm run build`, and audit for credentials, infrastructure details, and text that presents the internal test game or its code as something readers can use.
