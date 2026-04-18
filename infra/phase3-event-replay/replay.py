"""Phase 3 event fixture replay loader for the Phase 4 local walkthrough.

Reads fixtures/phase3-events.jsonl and publishes each line as a single JSON
message to the local broker exchange `phase3.session.events`. Replay is
idempotent because `(tenant_id, source_event_id)` is the ingestion unique key;
re-running this script against the same broker produces zero new
ProgressRecord rows on the consumer side.

Dependencies (stdlib only):
    python3 -m infra.phase3_event_replay.replay \
        --broker amqp://muallimi:muallimi_local@localhost:5672/ \
        --exchange phase3.session.events
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

DEFAULT_FIXTURE = Path(__file__).parent / "fixtures" / "phase3-events.jsonl"


def load_events(path: Path) -> list[dict]:
    if not path.exists():
        raise FileNotFoundError(f"Fixture not found at {path}")
    events: list[dict] = []
    for line_no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        line = line.strip()
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError as exc:
            raise ValueError(f"Invalid JSON on line {line_no}: {exc}") from exc
    return events


def publish(events: list[dict], broker: str, exchange: str) -> int:
    try:
        import pika  # type: ignore
    except ModuleNotFoundError:
        print(
            "pika is not installed; dumping events to stdout instead. "
            "Install pika to actually publish.",
            file=sys.stderr,
        )
        for event in events:
            print(json.dumps(event))
        return len(events)
    connection = pika.BlockingConnection(pika.URLParameters(broker))
    try:
        channel = connection.channel()
        channel.exchange_declare(exchange=exchange, exchange_type="topic", durable=True)
        published = 0
        for event in events:
            routing_key = event.get("event_kind", "unknown")
            channel.basic_publish(
                exchange=exchange,
                routing_key=routing_key,
                body=json.dumps(event).encode("utf-8"),
            )
            published += 1
        return published
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description="Replay Phase 3 session events onto the local broker.")
    parser.add_argument("--broker", default=os.environ.get("BROKER_URL", "amqp://muallimi:muallimi_local@localhost:5672/"))
    parser.add_argument("--exchange", default="phase3.session.events")
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    args = parser.parse_args()

    events = load_events(args.fixture)
    published = publish(events, args.broker, args.exchange)
    print(f"Replayed {published} events from {args.fixture}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
