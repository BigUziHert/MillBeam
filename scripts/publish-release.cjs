const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

// Called by actions/github-script. The token remains inside the Actions job;
// neither the public release nor the desktop updater receives a credential.
module.exports = async function publish({ github, context, core }) {
  const { owner, repo } = context.repo;
  if (`${owner}/${repo}` !== 'BigUziHert/MillBeam' || context.eventName === 'pull_request')
    throw new Error('Only the MillBeam repository may publish these updates.');
  const dev = context.ref === 'refs/heads/dev';
  const version = process.env.MILLBEAM_VERSION;
  const validVersion = dev ? /^\d+\.\d+\.\d+-dev\.[1-9]\d*\.[1-9]\d*$/ : /^\d+\.\d+\.\d+$/;
  if (!validVersion.test(version || '') || (!dev && context.ref !== `refs/tags/v${version}`))
    throw new Error('The update version does not match its source ref.');
  const tag = `v${version}`;
  const directory = path.resolve('release/distributables');
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'release-manifest.json'), 'utf8').replace(/^\uFEFF/, ''));
  if (manifest.version !== version || manifest.commit !== context.sha || manifest.sourceRef !== context.ref)
    throw new Error('The tested artifact does not match this source commit and version.');
  const names = ['MillBeam.exe', 'MillBeam-Windows-x64.zip', 'SHA256SUMS.txt'];
  const files = names.map(name => {
    const data = fs.readFileSync(path.join(directory, name));
    if (!data.length) throw new Error(`Empty release asset: ${name}`);
    return { name, data, size: data.length, digest: `sha256:${crypto.createHash('sha256').update(data).digest('hex')}` };
  });
  const sums = files[2].data.toString('utf8').replace(/\r/g, '').trim().split('\n');
  for (const file of files.slice(0, 2)) {
    if (!sums.includes(`${file.digest.slice(7)}  ${file.name}`))
      throw new Error(`The artifact checksum does not match ${file.name}.`);
  }
  async function mayPublish() {
    if (!dev) return true;
    const head = await github.rest.git.getRef({ owner, repo, ref: 'heads/dev' });
    if (head.data.object.sha !== context.sha) {
      core.info('A newer dev commit exists; retaining this build as an Actions artifact.');
      return false;
    }
    // A rerun of an older successful run must not publish behind a newer run
    // at the same commit. Compare numeric components, never release timestamps.
    const key = value => /^v?(\d+)\.(\d+)\.(\d+)-dev\.(\d+)\.(\d+)$/.exec(value)?.slice(1).map(Number);
    const candidate = key(version);
    const releases = await github.paginate(github.rest.repos.listReleases, { owner, repo, per_page: 100 });
    for (const release of releases) {
      if (release.draft) continue;
      const other = key(release.tag_name);
      if (!other) continue;
      const difference = other.map((part, index) => part - candidate[index]).find(part => part !== 0) || 0;
      if (difference > 0) {
        core.info('A newer dev version is already published; retaining this build as an Actions artifact.');
        return false;
      }
    }
    return true;
  }
  if (!await mayPublish()) return;
  const body = [
    `MillBeam ${version}, built from commit ${context.sha}.`,
    '',
    'This Windows x64 build passed controller tests, updater tests, and the packaged simulator UI and shutdown checks.',
    '',
    'Download MillBeam-Windows-x64.zip for the portable application and notices, or MillBeam.exe for the executable. In-app updates use the verified MillBeam.exe release asset.',
    'Updating requires disconnecting the controller first. The app reopens disconnected; machine settings and work offsets are not changed by an update.',
  ].join('\n');
  let release;
  try { release = (await github.rest.repos.getReleaseByTag({ owner, repo, tag })).data; }
  catch (error) { if (error.status !== 404) throw error; }
  if (release && release.target_commitish !== context.sha)
    throw new Error('This immutable release tag already belongs to another commit.');
  if (!release) {
    release = (await github.rest.repos.createRelease({
      owner, repo, tag_name: tag, target_commitish: context.sha,
      name: `MillBeam ${version}`, body, draft: true, prerelease: dev, make_latest: 'false',
    })).data;
  }
  const releaseId = release.id;
  let assets = await github.paginate(github.rest.repos.listReleaseAssets, { owner, repo, release_id: releaseId, per_page: 100 });
  for (const file of files) {
    const found = assets.find(asset => asset.name === file.name);
    if (found) {
      if (found.state !== 'uploaded' || found.size !== file.size || found.digest !== file.digest)
        throw new Error(`Existing immutable release asset does not match: ${file.name}`);
      continue;
    }
    if (!release.draft) throw new Error('A published release is incomplete; refusing to modify it.');
    await github.rest.repos.uploadReleaseAsset({
      owner, repo, release_id: releaseId, name: file.name,
      headers: { 'content-type': file.name.endsWith('.zip') ? 'application/zip' : 'application/octet-stream', 'content-length': file.size },
      data: file.data,
    });
  }
  assets = await github.paginate(github.rest.repos.listReleaseAssets, { owner, repo, release_id: releaseId, per_page: 100 });
  if (assets.length !== files.length || files.some(file => !assets.some(asset =>
    asset.name === file.name && asset.state === 'uploaded' && asset.size === file.size && asset.digest === file.digest)))
    throw new Error('GitHub did not confirm the complete release with matching SHA-256 digests. The release remains a draft.');
  if (!await mayPublish()) return;
  if (release.draft) {
    await github.rest.repos.updateRelease({ owner, repo, release_id: releaseId, draft: false, prerelease: dev, make_latest: dev ? 'false' : 'true' });
  }
  core.info(`Published verified update: https://github.com/${owner}/${repo}/releases/tag/${tag}`);
};
