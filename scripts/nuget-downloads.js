// Keeps an issue named "NuGet downloads" up to date with the download counts of this
// project's NuGet packages, and comments on it when a count goes up, so the repository
// owner is notified. Run by .github/workflows/downloads.yml through actions/github-script.

const PACKAGES = ['CopybookKit', 'CopybookKit.Tool'];
const TITLE = 'NuGet downloads';
const MARKER = /<!-- counts: (\{.*?\}) -->/;

// Returns { id: { total, version } } with null for a package that is not on NuGet.
// NuGet runs several copies of its search service, and they update download counts at
// different times, so one can report 0 while another reports the real figure. Every copy
// is asked, and the highest count wins.
async function readCounts(packages) {
  const index = await getJson('https://api.nuget.org/v3/index.json');
  const servers = [...new Set(index.resources
    .filter(r => r['@type'].startsWith('SearchQueryService'))
    .map(r => r['@id']))];
  if (servers.length === 0) {
    throw new Error('The NuGet service index lists no search service.');
  }

  const counts = {};
  for (const id of packages) {
    let best = null;
    let answered = 0;
    let lastError = null;
    for (const server of servers) {
      let result;
      try {
        result = await getJson(`${server}?q=packageid:${encodeURIComponent(id)}&prerelease=true&semVerLevel=2.0.0&take=1`);
      } catch (error) {
        // One copy being down is no reason to skip the day.
        lastError = error;
        continue;
      }

      answered++;
      const found = result.data.find(p => p.id.toLowerCase() === id.toLowerCase());
      if (found && (best === null || found.totalDownloads > best.total)) {
        best = { total: found.totalDownloads, version: found.version };
      }
    }

    if (answered === 0) {
      throw lastError;
    }

    counts[id] = best;
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
  const issues = await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'all', per_page: 100 });
  const issue = issues.find(i => !i.pull_request && i.title === TITLE && i.user && i.user.type === 'Bot');

  const match = issue ? MARKER.exec(issue.body || '') : null;
  const previous = match ? JSON.parse(match[1]) : { date: null, totals: {} };

  // Download counts never go down. A lower figure means a search copy is behind, so the
  // highest count seen so far is kept, and a later recovery is not reported as new downloads.
  for (const id of packages) {
    const before = previous.totals[id] || 0;
    if (counts[id] && counts[id].total < before) {
      counts[id] = { ...counts[id], total: before };
    }
  }

  const totals = Object.fromEntries(Object.entries(counts).map(([id, c]) => [id, Math.max(c ? c.total : 0, previous.totals[id] || 0)]));
  const body = makeBody(counts, totals, today, workflowUrl);

  if (!issue) {
    const created = await github.rest.issues.create({ owner, repo, title: TITLE, body });
    core.info(`Created issue #${created.data.number}: ${JSON.stringify(totals)}`);
    return;
  }

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
