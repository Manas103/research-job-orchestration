#!/bin/bash
# Measures "the environment came up from Terraform ... in under 6 minutes" end to end:
# terraform apply (building the API image, standing up the Postgres container, the network,
# and the API container), plus polling the API's own /health endpoint until it answers 200,
# which is the first point a human or a CI job could actually trust the environment. Tears
# the environment down again at the end either way, so nothing from this measurement is left
# running. Run from this directory inside WSL2 (needs a real Docker daemon and terraform).
set -euo pipefail
cd "$(dirname "$0")/../terraform"

START=$(date +%s.%N)
terraform apply -auto-approve -no-color

HEALTHY=0
for i in $(seq 1 60); do
  code=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:18080/health || true)
  if [ "$code" = "200" ]; then
    HEALTHY=1
    break
  fi
  sleep 1
done
END=$(date +%s.%N)

ELAPSED=$(echo "$END - $START" | bc)
echo "{\"healthy\": $([ $HEALTHY -eq 1 ] && echo true || echo false), \"elapsed_seconds\": $ELAPSED, \"claim\": \"under 6 minutes (360s)\", \"meets_claim\": $(echo "$ELAPSED < 360" | bc -l | sed 's/1/true/;s/0/false/')}"

terraform destroy -auto-approve -no-color >/dev/null
