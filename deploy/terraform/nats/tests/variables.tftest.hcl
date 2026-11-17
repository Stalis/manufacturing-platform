mock_provider "jetstream" {}

variables {
  nats_servers = "nats://127.0.0.1:4222"
}

run "reject_empty_servers" {
  command = plan
  variables {
    nats_servers = "   "
  }
  expect_failures = [var.nats_servers]
}

run "reject_zero_replicas" {
  command = plan
  variables {
    stream_replicas = 0
  }
  expect_failures = [var.stream_replicas]
}

run "reject_excess_replicas" {
  command = plan
  variables {
    stream_replicas = 6
  }
  expect_failures = [var.stream_replicas]
}

run "reject_fractional_replicas" {
  command = plan
  variables {
    stream_replicas = 1.5
  }
  expect_failures = [var.stream_replicas]
}

run "single_filters_are_not_sent_as_lists" {
  command = plan

  assert {
    condition = alltrue([
      for consumer in jetstream_consumer.consumers :
      consumer.filter_subjects == null ? true : length(consumer.filter_subjects) > 1
    ])
    error_message = "Singleton filter lists cause perpetual drift when NATS normalizes them to filter_subject."
  }
}
