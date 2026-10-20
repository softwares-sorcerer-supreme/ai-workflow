/** Router commands for the detail page of an issue; `repo` is `owner/name` as the API sends it. */
export function issueLink(repo: string, issueNumber: number): (string | number)[] {
  const [owner, name] = repo.split('/');

  return ['/issues', owner, name, issueNumber];
}
