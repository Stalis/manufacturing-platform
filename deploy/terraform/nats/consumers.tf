resource "jetstream_consumer" "consumers" {
  for_each = local.consumers

  durable_name = each.value.durable_name
  stream_id    = jetstream_stream.streams[each.value.stream_name].id
  # NATS normalizes a single filter to filter_subject; mirror that to keep plans stable.
  filter_subject  = length(each.value.filter_subjects) == 1 ? one(each.value.filter_subjects) : null
  filter_subjects = length(each.value.filter_subjects) > 1 ? each.value.filter_subjects : null
  ack_policy      = "explicit"
  deliver_all     = true
}
