/** "owner/repo#n": the identity of an issue or a PR across projects, for `track` and keyed state. */
export function issueKey(item: { repo: string; number: number }): string {
  return `${item.repo}#${item.number}`;
}

/** Splits `owner/name`, as the API sends a repo, into its two parts. */
export function splitRepo(repo: string): [owner: string, name: string] {
  const [owner, name] = repo.split('/');

  return [owner, name];
}

/** Router commands for the detail page of an issue; `repo` is `owner/name` as the API sends it. */
export function issueLink(repo: string, issueNumber: number): (string | number)[] {
  const [owner, name] = splitRepo(repo);

  return ['/issues', owner, name, issueNumber];
}
