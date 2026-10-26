resource "jetstream_consumer" "consumers" {
  for_each = local.consumers

  durable_name    = each.value.durable_name
  stream_id       = jetstream_stream.streams[each.value.stream_name].id
  filter_subjects = each.value.filter_subjects
  ack_policy      = "explicit"
  deliver_all     = true
}
