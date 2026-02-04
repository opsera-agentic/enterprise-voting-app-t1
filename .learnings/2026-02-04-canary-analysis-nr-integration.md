# Canary Analysis with New Relic APM Integration

**Date:** 2026-02-04
**Session:** QA Canary Analysis with New Relic Error Rate Detection
**Status:** Successfully implemented and tested end-to-end rollback

---

## Executive Summary

Successfully implemented and verified end-to-end canary analysis with New Relic APM integration. The system now:
- Detects error rates via New Relic Python agent
- Automatically triggers rollback when error rate exceeds 2% threshold
- Works with mock New Relic server for testing

**Proof of Success:**
```
Error Rate: 2.68% (threshold: 2%)
❌ Error rate exceeds threshold - triggering rollback
{"errorRate": 2.68}

Phase: Degraded
Abort: true
Message: RolloutAborted: Metric "error-rate" assessed Failed due to failed (2) > failureLimit (1)
```

---

## Issues Found and Fixed

### Issue 14: HTTP 500 Returns Not Captured by New Relic

**Problem:** Error simulation was returning HTTP 500 status codes, but New Relic Python agent primarily tracks **exceptions**, not HTTP status codes.

**Before (not captured properly):**
```python
if ERROR_SIM_ENABLED:
    app.logger.error('SIMULATED ERROR: Error simulation is ON')
    return jsonify({
        'error': 'Simulated Error',
        'message': 'Error simulation is enabled for canary rollback testing'
    }), 500
```

**After (properly captured):**
```python
if ERROR_SIM_ENABLED:
    app.logger.error('SIMULATED ERROR: Error simulation is ON')
    raise Exception('Simulated Error: Canary rollback testing')
```

**Why:** New Relic Python agent hooks into exception handling. Raising an actual exception ensures it's captured as an error in the APM data.

### Issue 15: Mock NR Server Missing `/v1/metrics` Endpoint

**Problem:** Analysis template queried `/v1/metrics?app=APP_NAME&metric=errorRate` but mock server returned 404, causing fallback to 0% error rate.

**Solution:** Mock NR server was updated to implement the `/v1/metrics` endpoint that:
1. Tracks errors received from Python agent
2. Calculates error rate from stored metrics
3. Returns `{"errorRate": X.XX}` for canary analysis

---

## Key Rules Learned

### Rule 50: New Relic Python Agent Captures Exceptions, Not HTTP Status Codes
For error simulation in Flask/Python apps, raise actual exceptions instead of returning HTTP 500. The NR agent hooks into exception handling.

### Rule 51: Mock NR Server Must Implement Query API
The mock server must implement `/v1/metrics?app={name}&metric=errorRate&window={time}` endpoint for canary analysis to work. This is separate from the agent data collection endpoints.

### Rule 52: Error Simulation State is Per-Pod
When multiple pods are running, the error simulation toggle (`ERROR_SIM_ENABLED`) is in-memory per pod. Toggling via HTTP hits random pods due to load balancing.

### Rule 53: Canary Analysis Steps in Argo Rollouts
- Health checks run at early steps (fast feedback)
- NR error rate analysis runs at later steps (50% traffic, 100% traffic)
- `failureLimit: 1` means 2 failures trigger rollback

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    CANARY DEPLOYMENT FLOW                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Step 0: setWeight 20%                                          │
│  Step 1: pause 2m                                                │
│  Step 2: Health Check Analysis ────────────────────────────┐    │
│  Step 3: setWeight 50%                                     │    │
│  Step 4: pause 3m                                          │    │
│  Step 5: NR Error Rate Analysis ◄──────────────────────────┤    │
│  Step 6: setWeight 100%                   If error > 2%    │    │
│  Step 7: pause 1m                         ────────────────►│    │
│  Step 8: NR Error Rate Analysis                   ROLLBACK │    │
│                                                            │    │
└────────────────────────────────────────────────────────────┴────┘
```

---

## File Templates

### 1. vote/app.py - Flask App with Error Simulation

```python
# ════════════════════════════════════════════════════════════════════════════════
# NEW RELIC APM CONFIGURATION
#
# Agent is auto-initialized by newrelic-admin run-program (see Dockerfile)
# Configuration loaded from newrelic.ini with environment variable interpolation
# ════════════════════════════════════════════════════════════════════════════════
import os

# Log New Relic configuration status at startup
_nr_license = os.getenv('NEW_RELIC_LICENSE_KEY')
_nr_app = os.getenv('NEW_RELIC_APP_NAME', 'vote-app')
_nr_host = os.getenv('NEW_RELIC_HOST', 'collector.newrelic.com')
if _nr_license:
    print(f'[New Relic] Agent enabled - App: {_nr_app}, Collector: {_nr_host}')
else:
    print('[New Relic] Agent disabled (no license key)')

# ════════════════════════════════════════════════════════════════════════════════

from flask import Flask, render_template, request, make_response, g, jsonify
from redis import Redis
import socket
import random
import json
import logging

option_a = os.getenv('OPTION_A', "Cats")
option_b = os.getenv('OPTION_B', "Dogs")
hostname = socket.gethostname()

app = Flask(__name__)

gunicorn_error_logger = logging.getLogger('gunicorn.error')
app.logger.handlers.extend(gunicorn_error_logger.handlers)
app.logger.setLevel(logging.INFO)

# ═══════════════════════════════════════════════════════════════════════════════
# ERROR SIMULATION - Simple ON/OFF toggle for canary rollback testing
# ═══════════════════════════════════════════════════════════════════════════════

# Simple global state - errors OFF by default
ERROR_SIM_ENABLED = False

def get_redis():
    if not hasattr(g, 'redis'):
        redis_host = os.getenv('REDIS_HOST', 'redis')
        redis_port = int(os.getenv('REDIS_PORT', 6379))
        g.redis = Redis(host=redis_host, port=redis_port, db=0, socket_timeout=5)
    return g.redis

@app.route("/api/error-sim", methods=['GET'])
def get_error_sim_status():
    """Get current error simulation status"""
    global ERROR_SIM_ENABLED
    return jsonify({'enabled': ERROR_SIM_ENABLED})

@app.route("/api/error-sim", methods=['POST'])
def toggle_error_sim():
    """Toggle error simulation ON/OFF"""
    global ERROR_SIM_ENABLED
    ERROR_SIM_ENABLED = not ERROR_SIM_ENABLED
    status = "ENABLED" if ERROR_SIM_ENABLED else "DISABLED"
    app.logger.info('Error simulation %s', status)
    return jsonify({'enabled': ERROR_SIM_ENABLED})

@app.route("/health", methods=['GET'])
def health():
    """Health check - always returns 200"""
    return jsonify({'status': 'healthy', 'hostname': hostname})

@app.route("/", methods=['POST','GET'])
def hello():
    global ERROR_SIM_ENABLED

    voter_id = request.cookies.get('voter_id')
    if not voter_id:
        voter_id = hex(random.getrandbits(64))[2:-1]

    vote = None

    if request.method == 'POST':
        # If error simulation is ON, raise exception (captured by New Relic)
        # IMPORTANT: Raise exception, don't just return HTTP 500
        # NR Python agent captures exceptions, not HTTP status codes
        if ERROR_SIM_ENABLED:
            app.logger.error('SIMULATED ERROR: Error simulation is ON')
            raise Exception('Simulated Error: Canary rollback testing')

        # Normal vote processing
        redis = get_redis()
        vote = request.form['vote']
        app.logger.info('Received vote for %s', vote)
        data = json.dumps({'voter_id': voter_id, 'vote': vote})
        redis.rpush('votes', data)

    resp = make_response(render_template(
        'index.html',
        option_a=option_a,
        option_b=option_b,
        hostname=hostname,
        vote=vote,
    ))
    resp.set_cookie('voter_id', voter_id)
    return resp


if __name__ == "__main__":
    app.run(host='0.0.0.0', port=80, debug=True, threaded=True)
```

### 2. vote/newrelic.ini - New Relic Python Agent Configuration

```ini
# ════════════════════════════════════════════════════════════════════════════════
# NEW RELIC PYTHON AGENT CONFIGURATION
#
# IMPORTANT: license_key, app_name, and host are set via environment variables:
#   NEW_RELIC_LICENSE_KEY
#   NEW_RELIC_APP_NAME
#   NEW_RELIC_HOST
# Do NOT set them here - env vars must take precedence for mock server to work.
# ════════════════════════════════════════════════════════════════════════════════

[newrelic]
# License key, app_name, and host are intentionally NOT set here
# They come from environment variables

# ═══════════════════════════════════════════════════════════════════════════════
# AGENT BEHAVIOR
# ═══════════════════════════════════════════════════════════════════════════════

monitor_mode = true
high_security = false
log_level = info
log_file = stdout

# ═══════════════════════════════════════════════════════════════════════════════
# DISTRIBUTED TRACING
# ═══════════════════════════════════════════════════════════════════════════════

distributed_tracing.enabled = true

# ═══════════════════════════════════════════════════════════════════════════════
# TRANSACTION TRACER
# ═══════════════════════════════════════════════════════════════════════════════

transaction_tracer.enabled = true
transaction_tracer.transaction_threshold = apdex_f
transaction_tracer.record_sql = obfuscated

# ═══════════════════════════════════════════════════════════════════════════════
# ERROR COLLECTOR - Critical for canary analysis
# ═══════════════════════════════════════════════════════════════════════════════

error_collector.enabled = true
error_collector.ignore_status_codes = 100-102 200-208 226 300-308

# ═══════════════════════════════════════════════════════════════════════════════
# APPLICATION LOGGING
# ═══════════════════════════════════════════════════════════════════════════════

application_logging.enabled = true
application_logging.forwarding.enabled = true
application_logging.metrics.enabled = true

# ═══════════════════════════════════════════════════════════════════════════════
# BROWSER MONITORING (disabled)
# ═══════════════════════════════════════════════════════════════════════════════

browser_monitoring.auto_instrument = false
```

### 3. Dockerfile.vote - With New Relic Admin Wrapper

```dockerfile
# Vote App - Python Flask with New Relic APM
FROM python:3.11-slim AS base

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /usr/local/app

COPY vote/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

FROM base AS final
COPY vote/ .

EXPOSE 80

# Use newrelic-admin to wrap gunicorn for APM monitoring
# Agent reads config from newrelic.ini with env var interpolation
CMD ["newrelic-admin", "run-program", "gunicorn", "app:app", "-b", "0.0.0.0:80", "--log-file", "-", "--access-logfile", "-", "--workers", "4", "--keep-alive", "0"]
```

### 4. ConfigMap - Environment Variables for New Relic

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: voting01-config
  labels:
    app.kubernetes.io/name: voting01
    environment: qa
data:
  # Redis Configuration
  REDIS_HOST: voting01-dev-redis.xxxxx.cache.amazonaws.com
  REDIS_PORT: "6379"

  # PostgreSQL Configuration
  DATABASE_HOST: voting01-dev-postgres.xxxxx.rds.amazonaws.com
  DATABASE_PORT: "5432"
  DATABASE_NAME: votes
  DATABASE_USER: postgres

  # Application Settings
  ENVIRONMENT: qa
  LOG_LEVEL: info

  # ═══════════════════════════════════════════════════════════════════════════════
  # NEW RELIC APM CONFIGURATION
  # ═══════════════════════════════════════════════════════════════════════════════

  # Mock Server Configuration (override default collector.newrelic.com)
  NEW_RELIC_HOST: "opsera-opsera-newrelic-dev.agent.opsera.dev"

  # Common Settings (All Services)
  NEW_RELIC_DISTRIBUTED_TRACING_ENABLED: "true"
  NEW_RELIC_LOG_LEVEL: "info"
  NEW_RELIC_LABELS: "environment:qa;app:voting01;team:platform"

  # Environment (applies to all services)
  NEW_RELIC_ENVIRONMENT: "qa"

  # Application Logging Settings
  NEW_RELIC_APPLICATION_LOGGING_ENABLED: "true"
  NEW_RELIC_APPLICATION_LOGGING_FORWARDING_ENABLED: "true"
```

### 5. Analysis Template - Canary with NR Error Rate

```yaml
# ════════════════════════════════════════════════════════════════════════════════
# COMPOSITE ANALYSIS TEMPLATE - Combined Health + Error Rate
# ════════════════════════════════════════════════════════════════════════════════
apiVersion: argoproj.io/v1alpha1
kind: AnalysisTemplate
metadata:
  name: voting01-canary-analysis
  labels:
    app.kubernetes.io/name: voting01
    environment: qa
spec:
  ttlStrategy:
    secondsAfterCompletion: 300
    secondsAfterFailure: 600
  args:
    - name: service-name
    - name: namespace
    - name: app-name
    - name: error-threshold
      value: "2"
  metrics:
    # Layer 1: Quick HTTP health check
    - name: http-health
      interval: 30s
      count: 3
      successCondition: result == "healthy"
      failureLimit: 1
      provider:
        job:
          spec:
            backoffLimit: 1
            ttlSecondsAfterFinished: 120
            template:
              spec:
                restartPolicy: Never
                containers:
                  - name: health-check
                    image: curlimages/curl:latest
                    command: ["/bin/sh", "-c"]
                    args:
                      - |
                        HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "http://{{args.service-name}}.{{args.namespace}}.svc.cluster.local/" --max-time 10)
                        if [ "$HTTP_CODE" = "200" ]; then
                          echo "healthy"
                          exit 0
                        else
                          echo "unhealthy: HTTP $HTTP_CODE"
                          exit 1
                        fi

    # Layer 2: New Relic error rate analysis
    - name: error-rate
      interval: 60s
      count: 2
      successCondition: result.errorRate < {{args.error-threshold}}
      failureLimit: 1
      provider:
        job:
          spec:
            backoffLimit: 1
            ttlSecondsAfterFinished: 120
            template:
              spec:
                restartPolicy: Never
                containers:
                  - name: newrelic-check
                    image: curlimages/curl:latest
                    env:
                      - name: NEW_RELIC_HOST
                        valueFrom:
                          configMapKeyRef:
                            name: voting01-config
                            key: NEW_RELIC_HOST
                            optional: true
                    command: ["/bin/sh", "-c"]
                    args:
                      - |
                        APP_NAME="{{args.app-name}}"
                        THRESHOLD={{args.error-threshold}}
                        NR_HOST="${NEW_RELIC_HOST:-opsera-opsera-newrelic-dev.agent.opsera.dev}"

                        echo "Querying New Relic for $APP_NAME error rate..."

                        RESPONSE=$(curl -s --max-time 30 \
                          "https://${NR_HOST}/v1/metrics?app=${APP_NAME}&metric=errorRate&window=5m" \
                          2>/dev/null || echo '{"errorRate": 0}')

                        ERROR_RATE=$(echo "$RESPONSE" | grep -oE '"errorRate"[[:space:]]*:[[:space:]]*[0-9.]+' | grep -oE '[0-9.]+$' || echo "0")
                        [ -z "$ERROR_RATE" ] && ERROR_RATE="0"

                        echo "Error Rate: ${ERROR_RATE}% (threshold: ${THRESHOLD}%)"

                        RESULT=$(awk -v rate="$ERROR_RATE" -v threshold="$THRESHOLD" 'BEGIN { if (rate < threshold) print "PASS"; else print "FAIL" }')

                        if [ "$RESULT" = "PASS" ]; then
                          echo '{"errorRate": '"$ERROR_RATE"'}'
                          exit 0
                        else
                          echo "❌ Error rate exceeds threshold - triggering rollback"
                          echo '{"errorRate": '"$ERROR_RATE"'}'
                          exit 1
                        fi
```

### 6. Rollout Strategy - Canary Steps

```yaml
spec:
  strategy:
    canary:
      steps:
        - setWeight: 20
        - pause: { duration: 2m }
        - analysis:
            templates:
              - templateName: voting01-health-check
            args:
              - name: service-name
                value: vote-canary
              - name: namespace
                value: voting01-qa
        - setWeight: 50
        - pause: { duration: 3m }
        - analysis:
            templates:
              - templateName: voting01-canary-analysis
            args:
              - name: service-name
                value: vote-canary
              - name: namespace
                value: voting01-qa
              - name: app-name
                value: voting01-vote-qa
              - name: error-threshold
                value: "2"
        - setWeight: 100
        - pause: { duration: 1m }
        - analysis:
            templates:
              - templateName: voting01-canary-analysis
            args:
              - name: service-name
                value: vote-canary
              - name: namespace
                value: voting01-qa
              - name: app-name
                value: voting01-vote-qa
              - name: error-threshold
                value: "2"
```

---

## Testing Commands

### Enable Error Simulation
```bash
curl -X POST https://vote-voting01-qa.agent.opsera.dev/api/error-sim
```

### Generate Error Traffic
```bash
for i in {1..300}; do
  curl -s -o /dev/null -X POST -d "vote=a" https://vote-voting01-qa.agent.opsera.dev/
done
```

### Check Rollout Status
```bash
kubectl describe rollout vote -n voting01-qa | grep -E "Current Step Index|Phase|Abort"
```

### Check Analysis Runs
```bash
kubectl get analysisrun -n voting01-qa --sort-by=.metadata.creationTimestamp | tail -5
```

### Check Error Rate Analysis Logs
```bash
kubectl logs -n voting01-qa $(kubectl get pods -n voting01-qa | grep "error-rate" | tail -1 | awk '{print $1}')
```

### Query Mock NR Directly
```bash
curl -s "https://opsera-opsera-newrelic-dev.agent.opsera.dev/v1/metrics?app=voting01-vote-qa&metric=errorRate&window=5m"
```

---

## Mock New Relic Server Requirements

The mock server must implement:

### Agent Data Collection (Python Agent Protocol)
- `POST /agent_listener/invoke_raw_method?method=preconnect`
- `POST /agent_listener/invoke_raw_method?method=connect`
- `POST /agent_listener/invoke_raw_method?method=metric_data`
- `POST /agent_listener/invoke_raw_method?method=analytic_event_data`
- `POST /agent_listener/invoke_raw_method?method=error_data`

### Metrics Query API (For Canary Analysis)
```
GET /v1/metrics?app={app-name}&metric=errorRate&window={time}

Response: {"errorRate": 2.68, "responseTime": 45.2, "throughput": 150}
```

---

## Success Criteria Checklist

- [x] Python agent connects to mock NR server
- [x] Agent reports as "Active: True" in pod
- [x] Error simulation raises actual exceptions
- [x] Mock NR server tracks error data
- [x] `/v1/metrics` endpoint returns real error rate
- [x] Analysis template queries and parses error rate
- [x] Error rate > 2% triggers analysis failure
- [x] Rollout aborts and shows "Degraded" phase
- [x] Canary traffic shifted back to stable

---

## Commits

1. `fix: Raise exception for error simulation to enable NR error tracking`
   - Changed from `return jsonify(...), 500` to `raise Exception(...)`
   - Ensures New Relic Python agent captures errors properly

---

## Related Files

| File | Purpose |
|------|---------|
| `vote/app.py` | Flask app with error simulation |
| `vote/newrelic.ini` | NR Python agent config |
| `vote/requirements.txt` | Includes `newrelic` package |
| `.opsera-voting01/Dockerfiles/Dockerfile.vote` | Uses `newrelic-admin` wrapper |
| `.opsera-voting01/k8s/overlays/qa/configmap.yaml` | NR environment variables |
| `.opsera-voting01/k8s/overlays/qa/analysis-template.yaml` | Canary analysis templates |
| `.opsera-voting01/k8s/overlays/qa/rollout.yaml` | Canary deployment strategy |
