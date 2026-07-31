import * as esbuild from 'esbuild';

const watch = process.argv.includes('--watch');
const shared = {
  bundle: true,
  external: ['vscode'],
  format: 'cjs',
  platform: 'node',
  target: 'node20',
  sourcemap: true,
  logLevel: 'info'
};
const builds = [
  { ...shared, entryPoints: ['src/extension.ts'], outfile: 'dist/extension.js' },
  { ...shared, entryPoints: ['test/runTest.ts'], outfile: 'dist/test/runTest.js' },
  { ...shared, entryPoints: ['test/suite/index.ts'], outfile: 'dist/test/suite/index.js' }
];

if (watch) {
  const contexts = await Promise.all(builds.map(options => esbuild.context(options)));
  await Promise.all(contexts.map(context => context.watch()));
} else {
  await Promise.all(builds.map(options => esbuild.build(options)));
}
