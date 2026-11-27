import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { consistencyBoundaryFor, isDcbSlice, tagsOf } from '../boundary'
import { fieldTrail, questionsFor, scheduleLabel, timeSpanLabel, unmatchedFields } from '../fields'
import { actorsOf, hiddenSliceNames } from '../filters'
import { enrolmentModel } from './fixtures'

/**
 * jasperfx#1297 — Dynamic Consistency Boundaries on the canvas: a DCB slice marker, tag chips on event
 * cards, and the dcb.events per-command table for the selected slice.
 */
describe('consistency boundaries (jasperfx#1297)', () => {
  it('resolves the boundary of a command slice against the model', () => {
    const model = enrolmentModel()
    const boundary = consistencyBoundaryFor(model, model.slices![0])!

    expect(boundary.reads.map((r) => `${r.kind} ${r.type.name} by ${r.tag} from ${r.member}`)).toEqual([
      'Model CourseEnrolment by courseId from CourseId',
      'Projection StudentSchedule by studentId from StudentId'
    ])
    // The model read folds the aggregate's applied events; the projection read folds what its View consumes
    expect(boundary.reads[0].folded.map((t) => t.name)).toEqual(['StudentEnrolled', 'CourseOpened'])
    expect(boundary.reads[1].folded.map((t) => t.name)).toEqual(['StudentEnrolled', 'StudentWithdrew'])

    // Each query term: the slice's own tagged event, then what the read folds
    expect(boundary.query.map((t) => `${t.tag}: ${t.eventTypes.map((e) => e.name).join(', ')}`)).toEqual([
      'courseId: StudentEnrolled, CourseOpened',
      'studentId: StudentEnrolled, StudentWithdrew'
    ])
    expect(boundary.untagged.map((t) => t.name)).toEqual(['EnrolmentReport'])
    expect(boundary.refusals).toEqual(['Course is full', 'Student already enrolled'])
  })

  it('is null for a slice that reads nothing by tag', () => {
    const model = enrolmentModel()
    expect(isDcbSlice(model.slices![1])).toBe(false)
    expect(consistencyBoundaryFor(model, model.slices![1])).toBeNull()
  })

  it('finds the tags an event card carries, and none for a card of another kind', () => {
    const slice = enrolmentModel().slices![0]
    expect(tagsOf(slice, slice.elements![3])).toEqual(['courseId', 'studentId'])
    expect(tagsOf(slice, slice.elements![2])).toEqual([])
  })

  it('marks the DCB column and chips the event card', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })

    expect(wrapper.find('.em-slice[data-slice="EnrolStudent"]').attributes('data-dcb')).toBe('true')
    expect(wrapper.find('.em-slice[data-slice="StudentSchedule"]').attributes('data-dcb')).toBeUndefined()

    const event = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Event' && c.text().includes('StudentEnrolled'))!
    expect(event.attributes('data-tags')).toBe('courseId studentId')
    expect(event.findAll('.em-tag').map((t) => t.text())).toEqual(['courseId', 'studentId'])
    expect(event.attributes('title')).toContain('Tags: courseId, studentId')
  })

  it('shows the consistency-boundary table for the selected DCB slice, and nothing for another', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    expect(wrapper.find('[data-testid="consistency-boundary"]').exists()).toBe(false)

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'EnrolStudent')!.trigger('click')
    const panel = wrapper.find('[data-testid="consistency-boundary"]')
    expect(panel.exists()).toBe(true)
    expect(panel.findAll('.em-boundary-read')).toHaveLength(2)
    expect(panel.find('[data-testid="boundary-query"]').text()).toContain('{ types: [StudentEnrolled, CourseOpened], tags: [courseId:command.CourseId] }')
    expect(panel.find('[data-testid="boundary-untagged"]').text()).toContain('EnrolmentReport')
    expect(panel.findAll('.em-refusal').map((r) => r.text())).toEqual(['Course is full', 'Student already enrolled'])

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'StudentSchedule')!.trigger('click')
    expect(wrapper.find('[data-testid="consistency-boundary"]').exists()).toBe(false)
  })
})

/** jasperfx#1298 — actors, screens, schedules, external events and descriptions. */
describe('structural elements (jasperfx#1298)', () => {
  it('lists the actors a model names and filters by them', () => {
    const model = enrolmentModel()
    expect(actorsOf(model)).toEqual(['Student'])
    expect([...hiddenSliceNames(model, { actors: new Set(['Student']) })].sort()).toEqual([
      'RecordPayment', 'SendWeeklyDigest', 'StudentSchedule'
    ])
  })

  it('badges the trigger card with its actor and offers an actor chip', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })

    expect(wrapper.find('.em-slice[data-slice="EnrolStudent"]').attributes('data-actor')).toBe('Student')
    const badge = wrapper.find('[data-testid="card-actor"]')
    expect(badge.text()).toBe('Student')
    expect(badge.attributes('title')).toBe('Student — requires student')
    expect(wrapper.find('[data-testid="filter-actors"] .em-filter-chip').text()).toBe('Student')
  })

  it('draws the screen as a white framed card in the wireframe lane', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    const screen = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Screen')!
    expect(screen.text()).toBe('Course page')
    expect(screen.attributes('data-lane')).toBe('Wireframe')
    expect(screen.attributes('style')).toContain('background: #FFFFFF')
  })

  it('puts the cadence of a scheduled automation on its header', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    const schedule = wrapper.find('.em-slice[data-slice="SendWeeklyDigest"] [data-testid="slice-schedule"]')
    expect(schedule.text()).toBe('cron 0 9 * * MON')
    expect(wrapper.find('.em-slice[data-slice="EnrolStudent"] [data-testid="slice-schedule"]').exists()).toBe(false)
  })

  it('renders schedules in a reader’s units', () => {
    expect(scheduleLabel({ kind: 'Interval', every: '00:05:00' })).toBe('every 5m')
    expect(scheduleLabel({ kind: 'Interval', every: '7.00:00:00' })).toBe('every 7d')
    expect(scheduleLabel({ kind: 'Interval', every: '01:30:00' })).toBe('every 1h 30m')
    expect(scheduleLabel({ kind: 'Named', name: 'month-end' })).toBe('month-end')
    expect(scheduleLabel(null)).toBeNull()
    expect(timeSpanLabel('00:00:30.5000000')).toBe('30.5s')
    expect(timeSpanLabel('nonsense')).toBe('nonsense')
  })

  it('shows descriptions on hover, on the card and on the slice header', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    const event = wrapper.findAll('.em-card').find((c) => c.text().includes('StudentEnrolled') && c.attributes('data-kind') === 'Event')!
    expect(event.attributes('title')).toContain('The seat is taken.')

    const header = wrapper.findAll('.em-slice-name').find((s) => s.text() === 'EnrolStudent')!
    expect(header.attributes('title')).toBe('EnrolStudent\nA student takes a seat on a course with room left.')
  })
})

/** jasperfx#1299 — field shapes: the panel, the trail, and the unmatched mark. */
describe('field trails (jasperfx#1299)', () => {
  it('joins the model’s questions to a slice and a type', () => {
    const model = enrolmentModel()
    expect(questionsFor(model, 'EnrolStudent')).toHaveLength(1)
    expect(questionsFor(model, 'StudentSchedule')).toHaveLength(0)
    expect([...unmatchedFields(model, 'EnrolStudent', { name: 'StudentEnrolled' })]).toEqual(['Seat'])
  })

  it('follows a field by name and by mapping', () => {
    const model = enrolmentModel()
    const slice = model.slices![0]
    const event = slice.elements![3]

    // CourseId is on the command by name and on the read model's Courses through its mapping
    const byName = fieldTrail(model, slice, event, 'CourseId')!
    expect([...byName.elementIds].sort()).toEqual([
      'EnrolStudent/Command/School.EnrolStudent',
      'EnrolStudent/Event/School.StudentEnrolled'
    ])
    expect(byName.unmatched).toBe(false)

    const readModel = slice.elements![5]
    const byMapping = fieldTrail(model, slice, readModel, 'Courses')!
    expect([...byMapping.elementIds].sort()).toEqual([
      'EnrolStudent/Event/School.StudentEnrolled',
      'EnrolStudent/ReadModel/School.StudentSchedule'
    ])

    expect(fieldTrail(model, slice, event, 'Seat')!.unmatched).toBe(true)
    expect(fieldTrail(model, slice, event, 'Nope')).toBeNull()
  })

  it('badges the slice with its question count', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    const badge = wrapper.find('.em-slice[data-slice="EnrolStudent"] [data-testid="slice-questions"]')
    expect(badge.text()).toBe('?')
    expect(badge.attributes('title')).toContain('StudentEnrolled.Seat comes from neither')
    expect(wrapper.find('.em-slice[data-slice="StudentSchedule"] [data-testid="slice-questions"]').exists()).toBe(false)
  })

  it('opens the fields panel for a selected card, marks the unmatched field and lights the trail', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    expect(wrapper.find('[data-testid="field-panel"]').exists()).toBe(false)

    const event = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Event' && c.text().includes('StudentEnrolled'))!
    await event.trigger('click')

    const panel = wrapper.find('[data-testid="field-panel"]')
    expect(panel.exists()).toBe(true)
    expect(panel.findAll('.em-field').map((f) => f.attributes('data-field'))).toEqual(['CourseId', 'StudentId', 'EnrolledAt', 'Seat'])
    expect(panel.find('.em-field[data-field="Seat"]').attributes('data-unmatched')).toBe('true')
    expect(panel.find('.em-field[data-field="CourseId"]').attributes('data-unmatched')).toBeUndefined()
    expect(panel.find('.em-field[data-field="EnrolledAt"] [data-flag="generated"]').exists()).toBe(true)
    expect(panel.find('.em-field[data-field="StudentId"] [data-flag="pii"]').exists()).toBe(true)

    await panel.find('.em-field[data-field="CourseId"] .em-field-pick').trigger('click')
    const command = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Command')!
    const readModel = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'ReadModel' && c.text().includes('StudentSchedule'))!
    expect(command.attributes('data-trail')).toBe('on')
    expect(readModel.attributes('data-trail')).toBe('off')

    // Picking it again clears the trail
    await panel.find('.em-field[data-field="CourseId"] .em-field-pick').trigger('click')
    expect(command.attributes('data-trail')).toBeUndefined()
  })

  it('renders a card whose type has no shape without a panel', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: enrolmentModel() } })
    const projection = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Projection')!
    await projection.trigger('click')
    expect(wrapper.find('[data-testid="field-panel"]').exists()).toBe(false)
  })
})
