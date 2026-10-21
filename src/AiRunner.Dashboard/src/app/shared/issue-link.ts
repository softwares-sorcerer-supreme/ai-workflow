/** "owner/repo#n": the identity of an issue or a PR across projects, for `track` and keyed state. */
export function issueKey(item: { repo: string; number: number }): string {
  return `${item.repo}#${item.number}`;
}

/** Router commands for the detail page of an issue; `repo` is `owner/name` as the API sends it. */
export function issueLink(repo: string, issueNumber: number): (string | number)[] {
  const [owner, name] = repo.split('/');

  return ['/issues', owner, name, issueNumber];
}
