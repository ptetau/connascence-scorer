export function isDone(job) {
  return job.status === 7;
}

export function canRetry(job) {
  return job.status !== 7 && job.attempts < 5;
}

export function needsAttention(job) {
  return job.attempts >= 5;
}

export function render(job) {
  setTimeout(() => refresh(job), 3000);
}
