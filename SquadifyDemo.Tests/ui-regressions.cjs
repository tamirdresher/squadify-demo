const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', 'SquadifyDemo', 'wwwroot', 'index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/refresh\(\);\s*$/, '');
const css = html.match(/<style>([\s\S]*?)<\/style>/)[1];

function createPage() {
    const nodes = {};
    const context = {
        setInterval: () => 1,
        clearInterval() {},
        document: {
            getElementById(id) {
                return nodes[id] ??= { textContent: '', innerHTML: '', addEventListener() {} };
            }
        }
    };
    vm.createContext(context);
    vm.runInContext(script, context);
    return { context, nodes };
}

function cssRule(selector) {
    const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const rule = css.match(new RegExp(`${escaped}\\s*\\{([^}]+)\\}`));
    assert.ok(rule, `Missing CSS rule: ${selector}`);
    return rule[1];
}

for (const [status, color] of [
    ['Completed', 'success'],
    ['Failed', 'danger'],
    ['Cancelled', 'warning'],
    ['Running', 'border']
]) {
    test(`${status} final details use the correct result class and CSS border`, () => {
        const { context } = createPage();
        const rendered = context.renderRunCard({
            runId: 'synthetic',
            status,
            title: 'Synthetic incident',
            finalResult: 'Synthetic terminal detail',
            steps: [{ step: 'FinalResult', type: 'deterministic', output: 'Synthetic step detail' }]
        });
        const classes = [...rendered.matchAll(/<div class="step-card ([^"]+)">/g)].map(match => match[1]);
        assert.equal(classes.length, 2, 'Check both step and top-level final-result surfaces');
        const terminal = status !== 'Running';
        const selector = terminal ? `.final-result--${status.toLowerCase()}` : '.final-result';
        for (const classList of classes) {
            assert.equal(classList, terminal ? `final-result ${selector.slice(1)}` : 'final-result');
        }
        assert.match(cssRule(selector), new RegExp(`border-color:\\s*var\\(--${color}\\)`));
        if (status !== 'Completed') assert.doesNotMatch(cssRule(selector), /--success|#1a2a1a|#1a2a2a/);
        assert.doesNotMatch(cssRule('.final-result'), /--success|#1a2a1a|#1a2a2a/);
    });
}

for (const status of ['Failed', 'Cancelled', 'Completed']) {
    test(`${status} refresh preserves terminal detail and honest aggregate status`, async () => {
        const { context, nodes } = createPage();
        context.fetch = async endpoint => ({
            json: async () => endpoint === '/status' ? { activeCount: 0 } : [{
                runId: 'synthetic',
                status,
                title: 'Synthetic incident',
                finalResult: 'Synthetic terminal detail',
                steps: []
            }]
        });
        await context.refresh();
        assert.equal(nodes.statusDot.className, `status-dot ${status === 'Completed' ? 'completed' : 'failed'}`);
        assert.match(nodes.resultsContainer.innerHTML, /Synthetic terminal detail/);
        if (status === 'Cancelled') assert.doesNotMatch(nodes.resultsContainer.innerHTML, /running/);
    });
}
