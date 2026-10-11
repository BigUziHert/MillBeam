const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const publish = require('./publish-release.cjs');

const version = '1.5.0-dev.42.1';
const sha = 'a'.repeat(40);
const checksum = data => `sha256:${crypto.createHash('sha256').update(data).digest('hex')}`;

// No network or GitHub credentials: the Octokit surface below records writes
// in memory and the release files live only inside this test's private folder.
async function fixture(options, action) {
  const originalDirectory = process.cwd();
  const originalVersion = process.env.MILLBEAM_VERSION;
  const temporaryRoot = fs.realpathSync(os.tmpdir());
  const directory = fs.mkdtempSync(path.join(temporaryRoot, 'MillBeam-release-test-'));
  const dist = path.join(directory, 'release', 'distributables');
  fs.mkdirSync(dist, { recursive: true });
  const files = [['MillBeam.exe', Buffer.from('test executable')], ['MillBeam-Windows-x64.zip', Buffer.from('test archive')]];
  for (const [name, data] of files) fs.writeFileSync(path.join(dist, name), data);
  const sums = files.map(([name, data]) => `${checksum(data).slice(7)}  ${name}`).join('\n') + '\n';
  fs.writeFileSync(path.join(dist, 'SHA256SUMS.txt'), options.badChecksum ? sums.replace(/^[0-9a-f]{64}/, '0'.repeat(64)) : sums);
  fs.writeFileSync(path.join(dist, 'release-manifest.json'), JSON.stringify({ version, commit: sha, sourceRef: 'refs/heads/dev' }));
  const state = { created: [], uploaded: [], published: [], assets: [], messages: [], headChecks: 0 };
  const github = { rest: { git: {}, repos: {} }, paginate: async (method, args) => (await method(args)).data };
  github.rest.git.getRef = async () => {
    state.headChecks++;
    const stale = options.staleHead || (options.staleAfterUpload && state.headChecks > 1);
    return { data: { object: { sha: stale ? 'b'.repeat(40) : sha } } };
  };
  github.rest.repos.listReleases = async () => ({ data: [] });
  github.rest.repos.getReleaseByTag = async () => { throw Object.assign(new Error('Not found'), { status: 404 }); };
  github.rest.repos.createRelease = async args => {
    state.created.push(args);
    return { data: { ...args, id: 123, target_commitish: sha } };
  };
  github.rest.repos.listReleaseAssets = async () => ({ data: state.assets });
  github.rest.repos.uploadReleaseAsset = async args => {
    if (options.failAsset === args.name) throw new Error('Simulated upload failure');
    state.uploaded.push(args.name);
    const digest = options.badDigest === args.name ? 'sha256:' + '0'.repeat(64) : checksum(args.data);
    state.assets.push({ id: state.assets.length + 1, name: args.name, size: args.data.length, state: 'uploaded', digest });
    return { data: state.assets.at(-1) };
  };
  github.rest.repos.updateRelease = async args => {
    state.published.push(args);
    return { data: args };
  };
  const context = { repo: { owner: 'BigUziHert', repo: 'MillBeam' }, eventName: options.pullRequest ? 'pull_request' : 'push', ref: 'refs/heads/dev', sha };
  const core = { info: message => state.messages.push(message) };
  try {
    process.env.MILLBEAM_VERSION = version;
    process.chdir(directory);
    await action({ run: () => publish({ github, context, core }), state });
  } finally {
    process.chdir(originalDirectory);
    if (originalVersion === undefined) delete process.env.MILLBEAM_VERSION;
    else process.env.MILLBEAM_VERSION = originalVersion;
    // Recursive cleanup is limited to the exact directory created above.
    const resolved = fs.realpathSync(directory);
    assert.equal(path.dirname(resolved), temporaryRoot);
    assert.match(path.basename(resolved), /^MillBeam-release-test-[A-Za-z0-9]+$/);
    fs.rmSync(resolved, { recursive: true });
  }
}

test('complete verified uploads become a published prerelease', async () => {
  await fixture({}, async ({ run, state }) => {
    await run();
    assert.equal(state.created.length, 1);
    assert.equal(state.created[0].draft, true);
    assert.equal(state.created[0].tag_name, `v${version}`);
    assert.deepEqual(state.uploaded, ['MillBeam.exe', 'MillBeam-Windows-x64.zip', 'SHA256SUMS.txt']);
    assert.deepEqual(state.published.map(value => [value.draft, value.prerelease, value.make_latest]), [[false, true, 'false']]);
    assert.equal(state.headChecks, 2);
  });
});

test('bad local checksum is rejected before any GitHub write', async () => {
  await fixture({ badChecksum: true }, async ({ run, state }) => {
    await assert.rejects(run, /checksum does not match/);
    assert.equal(state.created.length, 0);
    assert.equal(state.uploaded.length, 0);
    assert.equal(state.published.length, 0);
  });
});

test('failed asset upload leaves the release unpublished', async () => {
  await fixture({ failAsset: 'MillBeam-Windows-x64.zip' }, async ({ run, state }) => {
    await assert.rejects(run, /Simulated upload failure/);
    assert.equal(state.created[0].draft, true);
    assert.deepEqual(state.uploaded, ['MillBeam.exe']);
    assert.equal(state.published.length, 0);
  });
});

test('incorrect GitHub asset digest leaves the release unpublished', async () => {
  await fixture({ badDigest: 'MillBeam.exe' }, async ({ run, state }) => {
    await assert.rejects(run, /matching SHA-256 digests/);
    assert.equal(state.uploaded.length, 3);
    assert.equal(state.published.length, 0);
  });
});

test('stale dev HEAD skips all release writes', async () => {
  await fixture({ staleHead: true }, async ({ run, state }) => {
    await run();
    assert.equal(state.created.length, 0);
    assert.equal(state.uploaded.length, 0);
    assert.equal(state.published.length, 0);
    assert.match(state.messages[0], /newer dev commit/);
  });
});

test('a changed dev HEAD during upload keeps the completed release in draft', async () => {
  await fixture({ staleAfterUpload: true }, async ({ run, state }) => {
    await run();
    assert.equal(state.uploaded.length, 3);
    assert.equal(state.headChecks, 2);
    assert.equal(state.published.length, 0);
  });
});

test('pull requests cannot publish even with a matching repository and ref', async () => {
  await fixture({ pullRequest: true }, async ({ run, state }) => {
    await assert.rejects(run, /Only the MillBeam repository/);
    assert.equal(state.headChecks, 0);
    assert.equal(state.created.length, 0);
    assert.equal(state.published.length, 0);
  });
});
