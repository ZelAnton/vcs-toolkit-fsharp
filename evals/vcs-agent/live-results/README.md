# Optional live results

Store opt-in live-model observation or result files in this directory. Artifacts are
ignored deliberately: live output can be nondeterministic, may contain
machine-specific evidence, and is never read by the ordinary CI job.

To assess a live observation explicitly, pass its path to
`scripts/record-vcs-agent-eval.ps1`, write the normalized result outside
`evals/vcs-agent/offline/`, and pass that result to
`scripts/check-vcs-agent-eval.ps1`. Review live artifacts before sharing them.
