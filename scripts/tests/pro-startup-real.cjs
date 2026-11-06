const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require(path.join(__dirname, '../../web-panel/node_modules/typescript'));

let checked = 0;
for (const name of fs.readdirSync(process.argv[2])) {
    if (!name.startsWith('app-') || !name.endsWith('.bundle.js')) continue;
    const source = fs.readFileSync(path.join(process.argv[2], name), 'utf8');
    if (!source.includes('logDispatchedActions')) continue;
    const ast = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
    function visit(node) {
        if (ts.isNewExpression(node) && node.arguments?.length === 2 &&
            ts.isObjectLiteralExpression(node.arguments[1]) && node.arguments[1].properties.some(property => property.name?.getText(ast) === 'logDispatchedActions')) {
            const expression = node.arguments[0].getText(ast);
            const binding = /\b([$\w]+)\.state\b/.exec(expression);
            assert.ok(binding, 'Expected a persisted state input');
            for (const account of [{ id: 'fixture', subscription: null }, null]) {
                const cached = { account, settings: { language: 'zh' } };
                const snapshot = JSON.stringify(cached);
                const actual = vm.runInNewContext(expression, { [binding[1]]: { state: cached } }, { timeout: 1000 });
                assert.equal(actual.account?.subscription?.state, account ? 'active' : undefined, 'Actual bundle startup must normalize cached free account before any network refresh');
                assert.equal(JSON.stringify(cached), snapshot, 'Preserve input');
                assert.equal(actual.settings.language, 'zh');
            }
            checked++;
        }
        ts.forEachChild(node, visit);
    }
    visit(ast);
}
assert.equal(checked, 1, 'Verify the actual store constructor exactly once');
console.log('Actual bundle startup passed: cached free account before network refresh; signed-out state preserved.');
