const assert = require('node:assert/strict');
const fs = require('node:fs');

(async () => {
    const source = fs.readFileSync(process.argv[2], 'utf8');
    const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
    const cached = { account: { id: 'fixture', subscription: null }, settings: { language: 'zh' } };
    const hydrated = module.hydrateStore(cached).state;
    assert.equal(hydrated.account.subscription?.state, 'active', 'Cached free account must be normalized before any API refresh or dispatched action');
    assert.equal(hydrated.account.id, 'fixture');
    assert.deepEqual(hydrated.settings, cached.settings);
    assert.equal(cached.account.subscription, null, 'Do not mutate persisted input');
    assert.equal(module.hydrateStore({ account: null }).state.account, null, 'Preserve signed-out state');
    const refreshed = module.accountReducer(hydrated, { payload: { id: 'fixture', subscription: null } });
    assert.equal(refreshed.account.subscription.state, 'active', 'Subsequent account refresh must preserve normalization');
    console.log('Pro startup passed: cached account before refresh, immutable input, signed-out state, later refresh.');
})().catch(error => { console.error(error); process.exitCode = 1; });
