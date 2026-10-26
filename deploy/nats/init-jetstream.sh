#!/bin/sh
set -eu

NATS_URL="${NATS_URL:-nats://nats:4222}"

echo "Waiting for NATS JetStream at ${NATS_URL}..."
until nats --server="${NATS_URL}" server check jetstream >/dev/null 2>&1; do
  sleep 1
done
echo "NATS JetStream is ready."

create_stream() {
  name="$1"
  retention="$2"
  subjects="$3"

  if nats --server="${NATS_URL}" stream info "${name}" >/dev/null 2>&1; then
    echo "Stream ${name} already exists, updating..."
    nats --server="${NATS_URL}" stream edit "${name}" --subjects="${subjects}" --force
  else
    echo "Creating stream ${name}..."
    nats --server="${NATS_URL}" stream add "${name}" \
      --subjects="${subjects}" \
      --retention="${retention}" \
      --storage=file \
      --replicas=1 \
      --defaults
  fi
}

create_consumer() {
  stream="$1"
  durable="$2"
  shift 2

  if nats --server="${NATS_URL}" consumer info "${stream}" "${durable}" >/dev/null 2>&1; then
    echo "Consumer ${stream}/${durable} already exists."
    return 0
  fi

  echo "Creating consumer ${stream}/${durable}..."
  set -- "$@"
  filter_args=""
  for subj in "$@"; do
    filter_args="${filter_args} --filter=${subj}"
  done

  # shellcheck disable=SC2086
  nats --server="${NATS_URL}" consumer add "${stream}" "${durable}" \
    --pull \
    --deliver=all \
    --ack=explicit \
    ${filter_args} \
    --defaults
}

# 1. Producer streams & consumers
create_stream "PRODUCTION_COMMANDS" "work" "manufacturing.producer.tasks.start.v1"
create_consumer "PRODUCTION_COMMANDS" "producer-worker" "manufacturing.producer.tasks.start.>"

create_stream "PRODUCTION_EVENTS" "limits" "manufacturing.producer.events.started.v1,manufacturing.producer.events.completed.v1,manufacturing.producer.events.failed.v1,manufacturing.producer.events.invalid.v1,manufacturing.producer.events.stopped.v1"
create_consumer "PRODUCTION_EVENTS" "orders-api-producer" "manufacturing.producer.events.>"
create_consumer "PRODUCTION_EVENTS" "gateway-api-producer" "manufacturing.producer.events.>"

# 2. Inventory streams & consumers
create_stream "INVENTORY_COMMANDS" "work" "manufacturing.inventory.tasks.reserve.v1,manufacturing.inventory.tasks.grab.v1"
create_consumer "INVENTORY_COMMANDS" "inventory-worker" "manufacturing.inventory.tasks.reserve.>" "manufacturing.inventory.tasks.grab.>"

create_stream "INVENTORY_EVENTS" "limits" "manufacturing.inventory.events.delivered.v1"
create_consumer "INVENTORY_EVENTS" "producer-worker-inventory" "manufacturing.inventory.events.delivered.>"

echo "All JetStream streams and consumers initialized successfully."
