// Keeps an issue named "NuGet downloads" up to date with the download counts of this
// project's NuGet packages, and comments on it when a count goes up, so the repository
// owner is notified. Run by .github/workflows/downloads.yml through actions/github-script.

const PACKAGES = ['CopybookKit', 'CopybookKit.Tool'];
const TITLE = 'NuGet downloads';
const MARKER = /<!-- counts: (\{.*?\}) -->/;

// Returns { id: { total, version } } with null for a package that is not on NuGet.
async function readCounts(packages) {
  const index = await getJson('https://api.nuget.org/v3/index.json');
  const search = index.resources.find(r => r['@type'].startsWith('SearchQueryService'));
  if (!search) {
    throw new Error('The NuGet service index lists no search service.');
  }

  const counts = {};
  for (const id of packages) {
    const result = await getJson(`${search['@id']}?q=packageid:${encodeURIComponent(id)}&prerelease=true&semVerLevel=2.0.0&take=1`);
    const found = result.data.find(p => p.id.toLowerCase() === id.toLowerCase());
    counts[id] = found ? { total: found.totalDownloads, version: found.version } : null;
  }
  return counts;
}

async function getJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${url} returned HTTP ${response.status}.`);
  }
  return response.json();
}

function makeBody(counts, totals, today, workflowUrl) {
  const rows = Object.entries(counts).map(([id, c]) => {
    const link = `[${id}](https://www.nuget.org/packages/${id})`;
    return c ? `| ${link} | ${c.version} | ${c.total.toLocaleString('en-US')} |` : `| ${id} | not on NuGet yet | 0 |`;
  });
  return [
    `Download counts of this project's NuGet packages, updated daily by the [NuGet downloads workflow](${workflowUrl}). When a count goes up, a comment is added here and the repository owner is notified.`,
    '',
    '| Package | Latest version | Total downloads |',
    '|---|---|---|',
    ...rows,
    '',
    'NuGet counts every download, including automated mirrors, package scanners and build servers, and updates its figures a few hours late. A new package usually gets some downloads from mirrors in its first days, so a small number is not proof of a real user.',
    '',
    `Last checked: ${today} (UTC).`,
    `<!-- counts: ${JSON.stringify({ date: today, totals })} -->`,
  ].join('\n');
}

module.exports = async function report({ github, context, core, packages = PACKAGES, now = new Date() }) {
  const { owner, repo } = context.repo;
  const today = now.toISOString().slice(0, 10);
  const workflowUrl = `https://github.com/${owner}/${repo}/actions/workflows/downloads.yml`;

  const counts = await readCounts(packages);
  const totals = Object.fromEntries(Object.entries(counts).map(([id, c]) => [id, c ? c.total : 0]));
  const body = makeBody(counts, totals, today, workflowUrl);

  const issues = await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'all', per_page: 100 });
  const issue = issues.find(i => !i.pull_request && i.title === TITLE && i.user && i.user.type === 'Bot');

  if (!issue) {
    const created = await github.rest.issues.create({ owner, repo, title: TITLE, body });
    core.info(`Created issue #${created.data.number}: ${JSON.stringify(totals)}`);
    return;
  }

  const match = MARKER.exec(issue.body || '');
  const previous = match ? JSON.parse(match[1]) : { date: null, totals: {} };

  const increases = packages
    .filter(id => totals[id] > (previous.totals[id] || 0))
    .map(id => {
      const before = previous.totals[id] || 0;
      return `- **${id}**: ${before.toLocaleString('en-US')} → ${totals[id].toLocaleString('en-US')} (+${(totals[id] - before).toLocaleString('en-US')})`;
    });

  await github.rest.issues.update({ owner, repo, issue_number: issue.number, body });

  if (increases.length > 0) {
    const since = previous.date ? ` since ${previous.date}` : '';
    await github.rest.issues.createComment({
      owner,
      repo,
      issue_number: issue.number,
      body: `@${owner} New NuGet downloads${since}:\n\n${increases.join('\n')}`,
    });
    core.info(`Commented on issue #${issue.number}: ${increases.length} package(s) went up.`);
  } else {
    core.info(`No new downloads. Issue #${issue.number} updated: ${JSON.stringify(totals)}`);
  }
};

module.exports.readCounts = readCounts;
